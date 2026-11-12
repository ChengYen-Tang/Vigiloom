using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Inventory;

public enum InventoryStatusResultKind { Success, NotRegistered, ServerMismatch, Unauthorized, Unavailable, Invalid, OwnerChanged }
public sealed record InventoryStatusResult(InventoryStatusResultKind Kind, IReadOnlyList<InventorySourceStatus>? Sources = null,
    AgentSettingsOwner? Owner = null, bool SupportsProcessSources = false);

/// <summary>Reads an authenticated central generation fence before allocating new inventory work.</summary>
public sealed class InventoryStatusClient(HttpClient http, TimeProvider clock)
{
    private const int MaximumResponseBytes = 8192;

    public async Task<InventoryStatusResult> ReadAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? InventoryStatusResultKind.ServerMismatch : InventoryStatusResultKind.NotRegistered);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, "api/v1/agent/inventory/status?contract=5"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{identity.AgentId!.Value:D}.{identity.MachineSecret}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(InventoryStatusResultKind.Unauthorized);
            if (response.StatusCode != HttpStatusCode.OK) return new(InventoryStatusResultKind.Unavailable);
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, linked.Token);
            var sources = await response.Content.ReadFromJsonAsync<InventorySourceStatus[]>(cancellationToken: linked.Token);
            if (sources is null || sources.Length > Enum.GetValues<InventorySource>().Length) return new(InventoryStatusResultKind.Invalid);
            var seen = new HashSet<InventorySource>();
            foreach (var source in sources)
            {
                if (source is null || !Enum.IsDefined(source.Source) || !seen.Add(source.Source) ||
                    source.HighWaterGeneration < 1 || source.ActiveGeneration is <= 0 || source.LastAttemptGeneration is <= 0 ||
                    source.ActiveGeneration > source.HighWaterGeneration || source.LastAttemptGeneration > source.HighWaterGeneration ||
                    source.ActiveGeneration is { } active && (active != source.HighWaterGeneration || source.LastAttemptGeneration >= active) ||
                    (source.LastAttemptGeneration is null) != (source.LastOutcome is null) ||
                    (source.LastAttemptGeneration is null) != (source.LastStatus is null) ||
                    source.LastOutcome is { } outcome && !Enum.IsDefined(outcome) ||
                    source.LastStatus is { } status && !Enum.IsDefined(status) ||
                    source.LastStatus == InventoryCompletion.Complete && source.LastOutcome != InventoryOutcome.Complete ||
                    source.LastStatus == InventoryCompletion.Partial && source.LastOutcome == InventoryOutcome.Complete ||
                    source.LastSnapshotId is { } snapshotId && (snapshotId.Version != 7 || source.LastAttemptGeneration is null) ||
                    (source.LastStatus == InventoryCompletion.Rejected) != (source.LastRejectionReason is not null) ||
                    source.LastRejectionReason is { } reason && !Enum.IsDefined(reason)) return new(InventoryStatusResultKind.Invalid);
            }
            var (current, _) = await IdentityClient.ReadRegisteredAsync(vault, expected, linked.Token);
            if (current?.AgentId != identity.AgentId) return new(InventoryStatusResultKind.OwnerChanged);
            var supportsProcessSources = response.Headers.TryGetValues("Vigiloom-Inventory-Contract", out var advertised) &&
                advertised.SequenceEqual(["5"], StringComparer.Ordinal);
            if (!supportsProcessSources && sources.Any(x => x.Source > InventorySource.HostVolumes))
                return new(InventoryStatusResultKind.Invalid);
            return new(InventoryStatusResultKind.Success, sources,
                new AgentSettingsOwner(identity.AgentId!.Value, expected.AbsoluteUri, vault.StateDirectory), supportsProcessSources);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(InventoryStatusResultKind.Unavailable); }
        catch (HttpRequestException) { return new(InventoryStatusResultKind.Unavailable); }
        catch (JsonException) { return new(InventoryStatusResultKind.Invalid); }
    }
}
