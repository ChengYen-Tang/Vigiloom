using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Synchronizes the registered Agent's last valid baseline policy.</summary>
public sealed class AgentBaselineSettingsClient(HttpClient http, TimeProvider clock)
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public async Task<AgentBaselineSettingsSyncResult> LoadSavedAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var saved = await new AgentBaselineSettingsStore(vault, expected, identity.AgentId!.Value).ReadAsync(token);
        return saved is null ? new(AgentSettingsSyncStatus.NoSavedSettings) : new(AgentSettingsSyncStatus.Unchanged, saved);
    }

    public async Task<AgentBaselineSettingsSyncResult> SyncAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentSettingsSyncStatus.ServerMismatch : AgentSettingsSyncStatus.NotRegistered);
        var id = identity.AgentId!.Value;
        var store = new AgentBaselineSettingsStore(vault, expected, id);
        var previous = await store.ReadAsync(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, "api/v1/agent/baseline-settings"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{id:D}.{identity.MachineSecret}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(AgentSettingsSyncStatus.Unauthorized);
            if (response.StatusCode != HttpStatusCode.OK) return new(AgentSettingsSyncStatus.Unavailable);
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token);
            var snapshot = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: timeout.Token);
            if (snapshot is null || !snapshot.IsValid()) return new(AgentSettingsSyncStatus.Invalid);
            try
            {
                if (previous?.Snapshot.Revision == snapshot.Revision)
                    return new(AgentSettingsSyncStatus.Unchanged, await store.ConfirmAsync(previous.SaveToken, timeout.Token));
                return new(AgentSettingsSyncStatus.Saved, await store.SaveAsync(snapshot, previous?.SaveToken, clock.GetUtcNow(), timeout.Token));
            }
            catch (AgentSettingsConflictException) { return new(AgentSettingsSyncStatus.Conflict, await store.ReadAsync(timeout.Token)); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (HttpRequestException) { return new(AgentSettingsSyncStatus.Unavailable); }
        catch (JsonException) { return new(AgentSettingsSyncStatus.Invalid); }
        catch (InvalidDataException) { return new(AgentSettingsSyncStatus.Invalid); }
    }
}

public sealed record AgentBaselineSettingsSyncResult(AgentSettingsSyncStatus Status, AgentSavedBaselineSettings? Settings = null);
