using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Sends authenticated heartbeats and ordered queue status for one Agent process.</summary>
public sealed class AgentHeartbeatClient(HttpClient http)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Guid? agentId;
    private Uri? serverUri;
    private AgentQueueSessionRequest? sessionRequest;
    private bool sessionEstablished;
    private bool sessionReplaced;
    private long sequence;

    public Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, CancellationToken token) => SendAsync(vault, server, null, token);

    public Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, AgentConfigurationObservation? configuration, CancellationToken token) =>
        SendAsync(vault, server, configuration, null, token);

    public async Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, AgentConfigurationObservation? configuration,
        AgentQueueCapacityObservation? capacityObservation, CancellationToken token) =>
        await SendAsync(vault, server, configuration, capacityObservation, null, token);

    public async Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, AgentConfigurationObservation? configuration,
        AgentQueueCapacityObservation? capacityObservation, Guid? metricLossWriter, CancellationToken token)
        => await SendAsync(vault, server, configuration, capacityObservation, metricLossWriter, null, token);

    public async Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, AgentConfigurationObservation? configuration,
        AgentQueueCapacityObservation? capacityObservation, Guid? metricLossWriter, AgentLogStatusObservation? logObservation, CancellationToken token)
        => await SendAsync(vault, server, configuration, capacityObservation, metricLossWriter, logObservation, null, token);

    public async Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, AgentConfigurationObservation? configuration,
        AgentQueueCapacityObservation? capacityObservation, Guid? metricLossWriter, AgentLogStatusObservation? logObservation,
        InventorySettingsObservation? inventoryObservation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var expected = IdentityClient.NormalizeServer(server);
            var (state, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
            if (state is null) return mismatch ? HeartbeatResult.ServerMismatch : HeartbeatResult.NotRegistered;
            var id = state.AgentId!.Value;
            var settingsReport = configuration is not null && configuration.Owner.Matches(id, expected, vault) ? configuration.Report : null;
            var capacityOwnerMatches = capacityObservation is not null && capacityObservation.Owner.Matches(id, expected, vault);
            var logReport = logObservation is not null && logObservation.Owner.Matches(id, expected, vault) && logObservation.Report.IsValid()
                ? logObservation.Report : null;
            var inventoryReport = inventoryObservation is not null && inventoryObservation.Owner.Matches(id, expected, vault) && inventoryObservation.Report.IsValid()
                ? inventoryObservation.Report : null;
            if (agentId != id || serverUri != expected)
            {
                agentId = id;
                serverUri = expected;
                sessionRequest = null;
                sessionEstablished = false;
                sessionReplaced = false;
                sequence = 0;
            }
            var credential = $"vla_{id:D}.{state.MachineSecret}";
            if (!sessionEstablished && !sessionReplaced)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await EstablishSessionAsync(expected, credential, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
                // Session discovery is optional: its failure must not suppress connectivity.
            }

            AgentHeartbeatReport? report = null;
            if (sessionEstablished)
            {
                try
                {
                    await using var lease = await vault.AcquireForTransportAsync(token);
                    var current = await lease.ReadAsync(token);
                    if (current?.AgentId != id || !string.Equals(current.Server, expected.AbsoluteUri, StringComparison.Ordinal))
                        throw new InvalidOperationException("Agent identity changed during heartbeat preparation.");
                    AgentQueueSnapshot? snapshot = null;
                    AgentQuarantineBreakdown? quarantine = null;
                    AgentQueueCapacityReport? capacityReport = null;
                    AgentMetricLossReport? metricLoss = null;
                    try
                    {
                        var (status, details) = new DurableBatchQueue(vault, id).ReadDiagnosticsLocked(lease);
                        var appliedCapacity = await AgentQueueCapacityStore.ReadLockedAsync(lease, id, expected.AbsoluteUri, token);
                        var capacity = appliedCapacity?.Snapshot.Capacity ?? AgentQueueCapacity.Default;
                        snapshot = new(status.TotalBytes, status.TotalBatches, status.QuarantinedBatches,
                            capacity.PayloadBytes, capacity.BatchCount);
                        quarantine = details;
                        if (capacityOwnerMatches) capacityReport = new(appliedCapacity?.Snapshot, appliedCapacity?.AppliedAt, capacityObservation!.Failure);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
                    {
                        // Unavailable differs from an empty queue; do not claim capacity was applied.
                        if (capacityOwnerMatches) capacityReport = new(null, null, AgentConfigurationFailure.LocalStorage);
                    }
                    try
                    {
                        var owner = new AgentSettingsOwner(id, expected.AbsoluteUri, vault.StateDirectory);
                        var loss = metricLossWriter is { Version: 7 } writer ? MetricLossLedger.ReadLocked(lease, owner, writer) : null;
                        if (loss is not null) metricLoss = new(loss.LedgerId, loss.StartedAt, loss.Batches, loss.Records, loss.CompletenessUnknown);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
                    {
                        // An unavailable ledger is unknown, not an empty loss snapshot.
                    }
                    report = new AgentHeartbeatReport(1, sessionRequest!.Epoch, checked(++sequence), snapshot, settingsReport, quarantine, capacityReport, metricLoss, logReport, inventoryReport);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
                {
                    // Connectivity is still reported when local state cannot be read.
                }
            }
            var candidate = report;
            while (true)
            {
                using var response = await PostHeartbeatAsync(expected, credential, candidate, token);
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && candidate?.InventoryConfiguration is not null)
                {
                    candidate = candidate with { InventoryConfiguration = null };
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && candidate?.LogStatus is not null)
                {
                    candidate = candidate with { LogStatus = null };
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.BadRequest || candidate is null)
                    return await HandleResponseAsync(response, expected, credential, candidate, token);
                // Each retry removes exactly one optional group while preserving the same snapshot and sequence.
                if (candidate.InventoryConfiguration is not null) candidate = candidate with { InventoryConfiguration = null };
                else if (candidate.LogStatus is not null) candidate = candidate with { LogStatus = null };
                else if (candidate.MetricLoss is not null) candidate = candidate with { MetricLoss = null };
                else if (candidate.CapacityConfiguration is not null) candidate = candidate with { CapacityConfiguration = null };
                else if (candidate.Quarantine is not null) candidate = candidate with { Quarantine = null };
                else if (candidate.Configuration is not null) candidate = candidate with { Configuration = null };
                else return await HandleResponseAsync(response, expected, credential, candidate, token);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<HeartbeatResult> HandleResponseAsync(HttpResponseMessage response, Uri expected, string credential,
        AgentHeartbeatReport? report, CancellationToken token)
    {
            if (report is not null && response.StatusCode == HttpStatusCode.Conflict)
            {
                sessionEstablished = false;
                sessionReplaced = true;
                using var fallback = await PostHeartbeatAsync(expected, credential, null, token);
                return fallback.StatusCode == HttpStatusCode.NoContent ? HeartbeatResult.SessionReplaced : Classify(fallback.StatusCode);
            }
            if (report is not null && response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                using var fallback = await PostHeartbeatAsync(expected, credential, null, token);
                return Classify(fallback.StatusCode);
            }
            return sessionReplaced && response.StatusCode == HttpStatusCode.NoContent ? HeartbeatResult.SessionReplaced : Classify(response.StatusCode);
    }

    private async Task EstablishSessionAsync(Uri server, string credential, CancellationToken token)
    {
        if (sessionRequest is null)
        {
            using var request = CreateRequest(HttpMethod.Get, new Uri(server, "api/v1/agent/queue-session"), credential);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return;
            await response.Content.LoadIntoBufferAsync(1024, token);
            var current = await response.Content.ReadFromJsonAsync<AgentQueueSession>(cancellationToken: token);
            if (current is null) return;
            sessionRequest = new AgentQueueSessionRequest(current.Epoch, Guid.CreateVersion7());
        }
        using var begin = CreateRequest(HttpMethod.Post, new Uri(server, "api/v1/agent/queue-session"), credential);
        begin.Content = JsonContent.Create(sessionRequest);
        using var result = await http.SendAsync(begin, HttpCompletionOption.ResponseHeadersRead, token);
        if (result.StatusCode == HttpStatusCode.NoContent) sessionEstablished = true;
        else if (result.StatusCode == HttpStatusCode.Conflict) sessionReplaced = true;
    }

    private Task<HttpResponseMessage> PostHeartbeatAsync(Uri server, string credential, AgentHeartbeatReport? report, CancellationToken token)
    {
        // SendAsync completes once headers arrive, so the request can be disposed by the caller below.
        return SendAsync();
        async Task<HttpResponseMessage> SendAsync()
        {
            using var request = CreateRequest(HttpMethod.Post, new Uri(server, "api/v1/agent/heartbeat"), credential);
            if (report is not null) request.Content = JsonContent.Create(report);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string credential)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static HeartbeatResult Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NoContent => HeartbeatResult.Accepted,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HeartbeatResult.Unauthorized,
        _ => HeartbeatResult.Unavailable
    };
}

public enum HeartbeatResult { Accepted, NotRegistered, ServerMismatch, Unauthorized, Unavailable, SessionReplaced }
