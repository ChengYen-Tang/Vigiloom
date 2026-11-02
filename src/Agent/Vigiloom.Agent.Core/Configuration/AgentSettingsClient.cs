using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Fetches and durably stores the registered Agent's transport scheduling settings.</summary>
public sealed class AgentSettingsClient(HttpClient http, TimeProvider clock)
{
    private const int MaximumResponseBytes = 4096;

    public static async Task<AgentSettingsOwner?> GetOwnerAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, _) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        return identity is null ? null : new(identity.AgentId!.Value, expected.AbsoluteUri, vault.StateDirectory);
    }

    /// <summary>Load the last-good snapshot without requiring a network connection.</summary>
    public async Task<AgentSettingsSyncResult> LoadSavedAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var saved = await new AgentSettingsStore(vault, expected, identity.AgentId!.Value).ReadAsync(token);
        return saved is null ? new(AgentSettingsSyncStatus.NoSavedSettings) : new(AgentSettingsSyncStatus.Unchanged, saved);
    }

    public async Task<AgentSettingsSyncResult> SyncAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var id = identity.AgentId!.Value;
        var store = new AgentSettingsStore(vault, expected, id);
        var previous = await store.ReadAsync(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, "api/v1/agent/settings"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{id:D}.{identity.MachineSecret}");
            if (previous is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{previous.Snapshot.Revision}\""));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (previous is null || !MatchesTag(response, previous.Snapshot.Revision)) return new(AgentSettingsSyncStatus.Invalid);
                return await FenceUnchangedAsync(store, previous, timeout.Token);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(AgentSettingsSyncStatus.Unauthorized);
            if (response.StatusCode != HttpStatusCode.OK) return new(AgentSettingsSyncStatus.Unavailable);
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token);
            var snapshot = await response.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: timeout.Token);
            if (snapshot is null || !snapshot.IsValid() || !MatchesTag(response, snapshot.Revision)) return new(AgentSettingsSyncStatus.Invalid);
            if (previous?.Snapshot.Revision == snapshot.Revision) return await FenceUnchangedAsync(store, previous, timeout.Token);
            try
            {
                var saved = await store.SaveAsync(snapshot, previous?.SaveToken, clock.GetUtcNow(), timeout.Token);
                return new(AgentSettingsSyncStatus.Saved, saved);
            }
            catch (AgentSettingsConflictException)
            {
                return new(AgentSettingsSyncStatus.Conflict, await store.ReadAsync(timeout.Token));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (HttpRequestException) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (JsonException) { return new(AgentSettingsSyncStatus.Invalid); }
    }

    private static bool MatchesTag(HttpResponseMessage response, string revision) =>
        response.Headers.ETag is { IsWeak: false } tag && string.Equals(tag.Tag, $"\"{revision}\"", StringComparison.Ordinal);

    private static async Task<AgentSettingsSyncResult> FenceUnchangedAsync(AgentSettingsStore store, AgentAppliedSettings previous, CancellationToken token)
    {
        try
        {
            // Even an unchanged observation must fence older in-flight responses.
            var fenced = await store.SaveAsync(previous.Snapshot, previous.SaveToken, previous.AppliedAt, token);
            return new(AgentSettingsSyncStatus.Unchanged, fenced);
        }
        catch (AgentSettingsConflictException)
        {
            return new(AgentSettingsSyncStatus.Conflict, await store.ReadAsync(token));
        }
    }
}

public enum AgentSettingsSyncStatus { Saved, Unchanged, NoSavedSettings, NotRegistered, ServerMismatch, Unauthorized, Unavailable, Invalid, Conflict }
public sealed record AgentSettingsSyncResult(AgentSettingsSyncStatus Status, AgentAppliedSettings? Settings = null);
public sealed record AgentSettingsOwner(Guid AgentId, string Server, string StateDirectory)
{
    public bool Matches(Guid agentId, Uri server, IdentityVault vault) => AgentId == agentId &&
        string.Equals(Server, server.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(StateDirectory, vault.StateDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
public sealed record AgentConfigurationObservation(AgentSettingsOwner Owner, AgentConfigurationReport Report);
