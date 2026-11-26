using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads host-owned Agent observations for a current Resources registration.</summary>
public sealed class HostAgentMonitoring(IDbContextFactory<HostMonitoringDbContext> contexts, TimeProvider clock)
{
    public async Task<SecurityObservation> GetSecurityObservationAsync(DeviceAgentState device, AgentRegistrationSummary? registration, CancellationToken token)
    {
        if (!device.Eligible || registration is null || registration.RevokedAt is not null || registration.DeviceGeneration != device.Generation)
            return new(device, null);
        await using var db = await contexts.CreateDbContextAsync(token);
        var status = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token);
        if (status is not null && (status.DeviceId != device.DeviceId || status.DeviceGeneration != device.Generation))
            throw new InvalidOperationException("Host status does not match the current Agent registration.");
        var observation = new SecurityAgentObservation(registration.Id, registration.CreatedAt, status?.LastHeartbeatAt,
            status?.HeartbeatSafetyIntervalSeconds ?? 30, status?.QueueReportEpoch, status?.QueueReportSequence,
            status?.InventoryConfigurationEpoch, status?.InventoryConfigurationSequence, status?.InventoryConfigurationReportedAt, status?.InventoryConfigurationJson,
            status?.IntegrityConfigurationEpoch, status?.IntegrityConfigurationSequence, status?.IntegrityConfigurationReportedAt, status?.IntegrityConfigurationJson,
            status?.BaselineConfigurationReportedAt, status?.BaselineConfigurationJson);
        return new(device, observation);
    }

    public async Task<HostLogStatusView?> GetLogStatusAsync(DeviceAgentState device, AgentRegistrationSummary? registration, CancellationToken token)
    {
        if (!device.Eligible || registration is null || registration.RevokedAt is not null || registration.DeviceGeneration != device.Generation) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var status = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token);
        if (status is null || status.DeviceId != device.DeviceId || status.DeviceGeneration != device.Generation ||
            status.LogStatusReportEpoch is null || status.LogStatusReportEpoch != status.QueueReportEpoch ||
            status.LogStatusReportSequence != status.QueueReportSequence || status.LogStatusReportedAt is not { } received ||
            status.LogStatusJson is not { Length: > 0 and <= 8192 } json) return null;
        try
        {
            var report = JsonSerializer.Deserialize<AgentLogStatusReport>(json);
            return report?.IsValid() == true ? new(received, Math.Max(AgentMonitoring.OfflineAfterSeconds, status.HeartbeatSafetyIntervalSeconds * 3), report) : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<AgentQueueCapacityStatus> GetQueueCapacityStatusAsync(DeviceAgentState device, AgentRegistrationSummary? registration, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rows = await db.Set<AgentQueueCapacitySetting>().FromSqlInterpolated($"SELECT * FROM [agent].[QueueCapacitySettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {device.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var desired = AgentQueueCapacityConfiguration.ToView(device.DeviceId, rows).Effective;
        QueueCapacityObservation? agent = null;
        if (device.Eligible && registration is not null && registration.RevokedAt is null && registration.DeviceGeneration == device.Generation)
        {
            var status = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token);
            if (status is not null && (status.DeviceId != device.DeviceId || status.DeviceGeneration != device.Generation))
                throw new InvalidOperationException("Host status does not match the current Agent registration.");
            agent = new QueueCapacityObservation(registration.CreatedAt, status?.LastHeartbeatAt, status?.HeartbeatSafetyIntervalSeconds ?? 30,
                status?.QueueReportEpoch, status?.QueueReportSequence, status?.QueueCapacityReportEpoch, status?.QueueCapacityReportSequence,
                status?.QueueCapacityReportedAt, status?.QueueCapacityAppliedRevision, status?.QueueCapacityAppliedBytes,
                status?.QueueCapacityAppliedBatches, status?.QueueCapacityAppliedAt, status?.QueueCapacityFailure);
        }
        var result = QueueCapacityStatusFormatter.Create(device, desired, agent, clock.GetUtcNow());
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<AgentSettingsStatus> GetSettingsStatusAsync(DeviceAgentState device, AgentRegistrationSummary? registration, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rows = await db.Set<AgentSetting>().FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {device.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var desired = AgentConfiguration.ToView(device.DeviceId, rows).Effective;
        AgentSettingsObservation? agent = null;
        if (device.Eligible && registration is not null && registration.RevokedAt is null && registration.DeviceGeneration == device.Generation)
        {
            var status = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token);
            if (status is not null && (status.DeviceId != device.DeviceId || status.DeviceGeneration != device.Generation))
                throw new InvalidOperationException("Host status does not match the current Agent registration.");
            agent = new AgentSettingsObservation(registration.CreatedAt, status?.LastHeartbeatAt, status?.HeartbeatSafetyIntervalSeconds ?? 30,
                status?.ConfigurationReportedAt, status?.AppliedConfigurationRevision, status?.AppliedHeartbeatSeconds,
                status?.AppliedSettingsPollSeconds, status?.AppliedBatchIntervalSeconds, status?.ConfigurationAppliedAt, status?.ConfigurationFailure);
        }
        var result = AgentSettingsStatusFormatter.Create(device, desired, agent, clock.GetUtcNow());
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, AgentConnectionStatus>> GetConnectionsAsync(IReadOnlyList<DeviceAgentOverview> devices, CancellationToken token)
    {
        var ids = devices.Where(x => x.Device.Eligible && x.Registration is not null).Select(x => x.Registration!.Id).ToArray();
        await using var db = await contexts.CreateDbContextAsync(token);
        var statuses = await db.AgentStatuses.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.DeviceId, x.DeviceGeneration, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds })
            .ToDictionaryAsync(x => x.Id, token);
        var now = clock.GetUtcNow();
        return devices.ToDictionary(x => x.Device.DeviceId, item =>
        {
            var device = item.Device;
            var registration = item.Registration;
            if (!device.Eligible) return new AgentConnectionStatus(device.DeviceId, AgentConnectionState.Disabled, null, AgentMonitoring.OfflineAfterSeconds);
            if (registration is null || registration.RevokedAt is not null || registration.DeviceGeneration != device.Generation)
                return new AgentConnectionStatus(device.DeviceId, AgentConnectionState.NotRegistered, null, AgentMonitoring.OfflineAfterSeconds);
            statuses.TryGetValue(registration.Id, out var status);
            if (status is not null && (status.DeviceId != device.DeviceId || status.DeviceGeneration != device.Generation))
                throw new InvalidOperationException("Host status does not match the current Agent registration.");
            var timeout = Math.Max(AgentMonitoring.OfflineAfterSeconds, (status?.HeartbeatSafetyIntervalSeconds ?? 30) * 3);
            var last = status?.LastHeartbeatAt;
            var basis = last ?? registration.CreatedAt;
            var state = basis > now ? AgentConnectionState.ClockUncertain
                : now - basis >= TimeSpan.FromSeconds(timeout) ? AgentConnectionState.Offline
                : last is null ? AgentConnectionState.AwaitingFirstHeartbeat : AgentConnectionState.Online;
            return new AgentConnectionStatus(device.DeviceId, state, last, timeout);
        });
    }

    public async Task<AgentConnectionStatus> GetAsync(DeviceAgentState device, AgentRegistrationSummary? registration, CancellationToken token)
    {
        if (!device.Eligible) return new(device.DeviceId, AgentConnectionState.Disabled, null, AgentMonitoring.OfflineAfterSeconds);
        if (registration is null || registration.RevokedAt is not null || registration.DeviceGeneration != device.Generation)
            return new(device.DeviceId, AgentConnectionState.NotRegistered, null, AgentMonitoring.OfflineAfterSeconds);

        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token)
            ?? new HostAgentStatus(registration.Id, device.DeviceId, device.Generation, registration.CreatedAt);
        if (agent.DeviceId != device.DeviceId || agent.DeviceGeneration != device.Generation)
            throw new InvalidOperationException("Host status does not match the current Agent registration.");
        var inbox = await db.InboxStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.Id, token);
        var now = clock.GetUtcNow();
        var offlineAfter = Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var basis = agent.LastHeartbeatAt ?? registration.CreatedAt;
        var state = basis > now ? AgentConnectionState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentConnectionState.Offline
            : agent.LastHeartbeatAt is null ? AgentConnectionState.AwaitingFirstHeartbeat : AgentConnectionState.Online;

        AgentQueueSnapshot? snapshot = agent.QueueTotalBytes is { } bytes && agent.QueueTotalBatches is { } batches &&
            agent.QueueQuarantinedBatches is { } quarantined && agent.QueueCapacityBytes is { } capacityBytes &&
            agent.QueueCapacityBatches is { } capacityBatches ? new(bytes, batches, quarantined, capacityBytes, capacityBatches) : null;
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

        var receipt = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == registration.Id && !EF.Property<bool>(x, "PendingDelivery"))
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
            .Select(x => new { x.EnqueuedAt, x.ReceivedAt, x.RowId }).FirstOrDefaultAsync(token);
        var enqueuedAt = inbox?.LastBatchEnqueuedAt;
        var receivedAt = inbox?.LastBatchReceivedAt;
        if (receipt is not null && (receivedAt is null || receipt.ReceivedAt > receivedAt ||
            receipt.ReceivedAt == receivedAt && receipt.RowId > (inbox?.LastBatchReceiptRowId ?? 0)))
            (enqueuedAt, receivedAt) = (receipt.EnqueuedAt, receipt.ReceivedAt);
        var batchTime = enqueuedAt is { } enqueued && receivedAt is { } received
            ? new AgentBatchTimeObservation(enqueued, received, enqueued - received > TimeSpan.FromMinutes(5)) : null;
        var usage = inbox?.InboxReceiptCount is { } count && inbox.InboxReceiptPayloadBytes is { } size
            ? (Count: count, Bytes: size) : await AgentReceiptUsage.ReadAsync(db, registration.Id, token);
        var capacity = new AgentReceiptCapacity(usage.Count, usage.Bytes, AgentBatchReceiver.MaximumRetainedBatchesPerAgent,
            AgentBatchReceiver.MaximumRetainedBytesPerAgent);
        var issue = inbox?.LastReplayIssue is { } reason && Enum.IsDefined(reason) && inbox.LastReplayIssueAt is { } issueAt
            ? new AgentReplayIssueObservation(reason, issueAt) : null;
        AgentModuleFailure[]? failures = null;
        try { if (agent.RuntimeModuleFailuresJson is { } json) failures = JsonSerializer.Deserialize<AgentModuleFailure[]>(json); }
        catch (JsonException) { }
        if (failures is not null && (failures.Length > 5 || failures.Any(x => x is null || !x.IsValid()))) failures = null;
        var process = agent.RuntimeReportedAt is { } runtimeAt && (agent.RuntimeCpuPercent is not null || agent.RuntimeWorkingSetBytes is not null || failures is not null)
            ? new AgentProcessMetrics(agent.RuntimeCpuPercent, agent.RuntimeWorkingSetBytes, runtimeAt, failures) : null;
        return new(device.DeviceId, state, agent.LastHeartbeatAt, offlineAfter, queue, batchTime?.ReceivedAt, batchTime, capacity, issue, metricLoss, process);
    }
}
