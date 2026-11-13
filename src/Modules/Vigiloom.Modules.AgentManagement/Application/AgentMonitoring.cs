using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads non-secret Agent connectivity independently of network Ping.</summary>
public sealed class AgentMonitoring(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    public const int OfflineAfterSeconds = 90;

    public async Task<AgentConnectionStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        if (!device.Eligible) return new(deviceId, AgentConnectionState.Disabled, null, OfflineAfterSeconds);
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.Id, x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.LastReplayIssue, x.LastReplayIssueAt, x.QueueReportedAt, x.QueueTotalBytes, x.QueueTotalBatches,
                x.QueueQuarantinedBatches, x.QueueCapacityBytes, x.QueueCapacityBatches,
                x.QuarantineInvalidLocal, x.QuarantineBadRequest, x.QuarantineConflict, x.QuarantineTooLarge,
                x.QuarantineUnsupported, x.QuarantineExpired, x.QuarantineOther,
                x.QueueReportEpoch, x.QueueReportSequence, x.QuarantineReportEpoch, x.QuarantineReportSequence,
                x.MetricLossReportEpoch, x.MetricLossReportSequence, x.MetricLossReportedAt, x.MetricLossLedgerId,
                x.MetricLossStartedAt, x.MetricLossBatches, x.MetricLossRecords, x.MetricLossCompletenessUnknown,
                x.LastBatchEnqueuedAt, x.LastBatchReceivedAt, x.LastBatchReceiptRowId, x.InboxReceiptCount, x.InboxReceiptPayloadBytes,
                LatestReceipt = db.ReceivedBatches.Where(batch => batch.AgentId == x.Id)
                    .OrderByDescending(batch => batch.ReceivedAt).ThenByDescending(batch => batch.RowId)
                    .Select(batch => new { batch.EnqueuedAt, batch.ReceivedAt, batch.RowId }).FirstOrDefault() }).SingleOrDefaultAsync(token);
        if (agent is null) return new(deviceId, AgentConnectionState.NotRegistered, null, OfflineAfterSeconds);
        var now = clock.GetUtcNow();
        var last = agent.LastHeartbeatAt;
        var offlineAfter = Math.Max(OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var state = ConnectionState(agent.CreatedAt, last, offlineAfter, now);
        AgentQueueSnapshot? snapshot = agent.QueueTotalBytes is { } bytes && agent.QueueTotalBatches is { } batches &&
                agent.QueueQuarantinedBatches is { } quarantined && agent.QueueCapacityBytes is { } capacityBytes &&
                agent.QueueCapacityBatches is { } capacityBatches ? new AgentQueueSnapshot(bytes, batches, quarantined, capacityBytes, capacityBatches) : null;
        AgentQuarantineBreakdown? details = agent.QuarantineInvalidLocal is { } invalid && agent.QuarantineBadRequest is { } bad &&
            agent.QuarantineConflict is { } conflict && agent.QuarantineTooLarge is { } tooLarge && agent.QuarantineUnsupported is { } unsupported &&
            agent.QuarantineExpired is { } expired && agent.QuarantineOther is { } other ? new(invalid, bad, conflict, tooLarge, unsupported, expired, other) : null;
        if (snapshot is null || agent.QueueReportEpoch is null || agent.QueueReportSequence is null ||
            agent.QuarantineReportEpoch != agent.QueueReportEpoch || agent.QuarantineReportSequence != agent.QueueReportSequence ||
            details?.IsValid(snapshot.QuarantinedBatches) != true) details = null;
        AgentQueueMonitoring? queue = agent.QueueReportedAt is { } reportedAt ? new(reportedAt, snapshot, details) : null;
        AgentMetricLossMonitoring? metricLoss = null;
        if (agent.MetricLossReportEpoch == agent.QueueReportEpoch && agent.MetricLossReportEpoch is not null &&
            agent.MetricLossReportSequence == agent.QueueReportSequence && agent.MetricLossReportedAt is { } lossReportedAt &&
            agent.MetricLossLedgerId is { } ledgerId && agent.MetricLossStartedAt is { } lossStartedAt &&
            agent.MetricLossBatches is { } lossBatches && agent.MetricLossRecords is { } lossRecords &&
            agent.MetricLossCompletenessUnknown is { } completenessUnknown)
        {
            var loss = new AgentMetricLossReport(ledgerId, lossStartedAt, lossBatches, lossRecords, completenessUnknown);
            if (loss.IsValid()) metricLoss = new(lossReportedAt, loss);
        }
        var enqueuedAt = agent.LastBatchEnqueuedAt;
        var receivedAt = agent.LastBatchReceivedAt;
        if (agent.LatestReceipt is { } receipt && (receivedAt is null || receipt.ReceivedAt > receivedAt ||
            receipt.ReceivedAt == receivedAt && receipt.RowId > (agent.LastBatchReceiptRowId ?? 0)))
        {
            enqueuedAt = receipt.EnqueuedAt;
            receivedAt = receipt.ReceivedAt;
        }
        var batchTime = enqueuedAt is { } enqueued && receivedAt is { } received
            ? new AgentBatchTimeObservation(enqueued, received, enqueued - received > TimeSpan.FromMinutes(5)) : null;
        var usage = agent.InboxReceiptCount is { } receiptCount && agent.InboxReceiptPayloadBytes is { } receiptBytes
            ? (Count: receiptCount, Bytes: receiptBytes) : await AgentReceiptUsage.ReadAsync(db, agent.Id, token);
        var capacity = new AgentReceiptCapacity(usage.Count, usage.Bytes, AgentBatchReceiver.MaximumRetainedBatchesPerAgent,
            AgentBatchReceiver.MaximumRetainedBytesPerAgent);
        var issue = agent.LastReplayIssue is { } reason && Enum.IsDefined(reason) && agent.LastReplayIssueAt is { } issueAt
            ? new AgentReplayIssueObservation(reason, issueAt) : null;
        return new(deviceId, state, last, offlineAfter, queue, batchTime?.ReceivedAt, batchTime, capacity, issue, metricLoss);
    }

    /// <summary>Reads only connectivity for a bounded set of already-resolved device generations.</summary>
    public async Task<IReadOnlyDictionary<Guid, AgentConnectionStatus>> GetConnectionsAsync(
        IReadOnlyList<DeviceAgentState> devicesToRead, CancellationToken token)
    {
        if (devicesToRead.Count > 250) throw new ArgumentOutOfRangeException(nameof(devicesToRead));
        var ids = devicesToRead.Where(x => x.Eligible).Select(x => x.DeviceId).ToArray();
        await using var db = await contexts.CreateDbContextAsync(token);
        var agents = await db.Agents.AsNoTracking().Where(x => ids.Contains(x.DeviceId) && x.RevokedAt == null)
            .Select(x => new { x.DeviceId, x.DeviceGeneration, x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds })
            .ToArrayAsync(token);
        var byDevice = agents.ToLookup(x => x.DeviceId);
        var now = clock.GetUtcNow();
        return devicesToRead.ToDictionary(x => x.DeviceId, device =>
        {
            if (!device.Eligible) return new AgentConnectionStatus(device.DeviceId, AgentConnectionState.Disabled, null, OfflineAfterSeconds);
            var agent = byDevice[device.DeviceId].SingleOrDefault(x => x.DeviceGeneration == device.Generation);
            if (agent is null) return new AgentConnectionStatus(device.DeviceId, AgentConnectionState.NotRegistered, null, OfflineAfterSeconds);
            var timeout = Math.Max(OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
            return new AgentConnectionStatus(device.DeviceId, ConnectionState(agent.CreatedAt, agent.LastHeartbeatAt, timeout, now), agent.LastHeartbeatAt, timeout);
        });
    }

    private static AgentConnectionState ConnectionState(DateTimeOffset createdAt, DateTimeOffset? last, int offlineAfter, DateTimeOffset now)
    {
        var basis = last ?? createdAt;
        return basis > now ? AgentConnectionState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentConnectionState.Offline
            : last is null ? AgentConnectionState.AwaitingFirstHeartbeat : AgentConnectionState.Online;
    }
}
