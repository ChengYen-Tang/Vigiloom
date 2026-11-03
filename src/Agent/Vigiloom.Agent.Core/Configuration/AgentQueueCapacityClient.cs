using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Synchronizes the independent queue-capacity group without changing transport-v1 settings.</summary>
public sealed class AgentQueueCapacityClient(HttpClient http, TimeProvider clock)
{
    private const int MaximumResponseBytes = 4096;

    public async Task<AgentQueueCapacitySyncResult> LoadSavedAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var saved = await new AgentQueueCapacityStore(vault, expected, identity.AgentId!.Value).ReadAsync(token);
        return saved is null ? new(AgentSettingsSyncStatus.NoSavedSettings) : new(AgentSettingsSyncStatus.Unchanged, saved);
    }

    public async Task<AgentQueueCapacitySyncResult> SyncAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var id = identity.AgentId!.Value;
        var store = new AgentQueueCapacityStore(vault, expected, id);
        var previous = await store.ReadAsync(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, "api/v1/agent/queue-capacity"));
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
            var snapshot = await response.Content.ReadFromJsonAsync<AgentQueueCapacitySnapshot>(cancellationToken: timeout.Token);
            if (snapshot is null || !snapshot.IsValid() || !MatchesTag(response, snapshot.Revision)) return new(AgentSettingsSyncStatus.Invalid);
            if (previous?.Snapshot.Revision == snapshot.Revision) return await FenceUnchangedAsync(store, previous, timeout.Token);
            try { return new(AgentSettingsSyncStatus.Saved, await store.SaveAsync(snapshot, previous?.SaveToken, clock.GetUtcNow(), timeout.Token)); }
            catch (AgentSettingsConflictException) { return new(AgentSettingsSyncStatus.Conflict, await store.ReadAsync(timeout.Token)); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (HttpRequestException) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (JsonException) { return new(AgentSettingsSyncStatus.Invalid); }
    }

    private static bool MatchesTag(HttpResponseMessage response, string revision) =>
        response.Headers.ETag is { IsWeak: false } tag && string.Equals(tag.Tag, $"\"{revision}\"", StringComparison.Ordinal);

    private static async Task<AgentQueueCapacitySyncResult> FenceUnchangedAsync(AgentQueueCapacityStore store, AgentAppliedQueueCapacity previous, CancellationToken token)
    {
        try { return new(AgentSettingsSyncStatus.Unchanged, await store.SaveAsync(previous.Snapshot, previous.SaveToken, previous.AppliedAt, token)); }
        catch (AgentSettingsConflictException) { return new(AgentSettingsSyncStatus.Conflict, await store.ReadAsync(token)); }
    }
}

public sealed record AgentQueueCapacitySyncResult(AgentSettingsSyncStatus Status, AgentAppliedQueueCapacity? Settings = null);
public sealed record AgentQueueCapacityObservation(AgentSettingsOwner Owner, AgentConfigurationFailure? Failure);
