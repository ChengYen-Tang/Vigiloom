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
            .Select(x => new { x.Id, x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.QueueReportedAt, x.QueueTotalBytes, x.QueueTotalBatches,
                x.QueueQuarantinedBatches, x.QueueCapacityBytes, x.QueueCapacityBatches,
                x.QuarantineInvalidLocal, x.QuarantineBadRequest, x.QuarantineConflict, x.QuarantineTooLarge,
                x.QuarantineUnsupported, x.QuarantineExpired, x.QuarantineOther,
                x.QueueReportEpoch, x.QueueReportSequence, x.QuarantineReportEpoch, x.QuarantineReportSequence,
                LastBatch = db.ReceivedBatches.Where(batch => batch.AgentId == x.Id)
                    .OrderByDescending(batch => batch.ReceivedAt).ThenByDescending(batch => batch.RowId)
                    .Select(batch => new { batch.EnqueuedAt, batch.ReceivedAt }).FirstOrDefault() }).SingleOrDefaultAsync(token);
        if (agent is null) return new(deviceId, AgentConnectionState.NotRegistered, null, OfflineAfterSeconds);
        var now = clock.GetUtcNow();
        var last = agent.LastHeartbeatAt;
        var basis = last ?? agent.CreatedAt;
        var offlineAfter = Math.Max(OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var state = basis > now ? AgentConnectionState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentConnectionState.Offline
            : last is null ? AgentConnectionState.AwaitingFirstHeartbeat : AgentConnectionState.Online;
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
        var batchTime = agent.LastBatch is { } batch ? new AgentBatchTimeObservation(batch.EnqueuedAt, batch.ReceivedAt,
            batch.EnqueuedAt - batch.ReceivedAt > TimeSpan.FromMinutes(5)) : null;
        var usage = await AgentReceiptUsage.ReadAsync(db, agent.Id, token);
        var capacity = new AgentReceiptCapacity(usage.Count, usage.Bytes, AgentBatchReceiver.MaximumRetainedBatchesPerAgent,
            AgentBatchReceiver.MaximumRetainedBytesPerAgent);
        return new(deviceId, state, last, offlineAfter, queue, batchTime?.ReceivedAt, batchTime, capacity);
    }
}
