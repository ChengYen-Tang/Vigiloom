using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Compares desired and independently reported applied queue capacity.</summary>
public sealed class AgentQueueCapacityMonitoring(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<AgentQueueCapacityStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.QueueReportEpoch, x.QueueReportSequence,
                x.QueueCapacityReportEpoch, x.QueueCapacityReportSequence, x.QueueCapacityReportedAt, x.QueueCapacityAppliedRevision,
                x.QueueCapacityAppliedBytes, x.QueueCapacityAppliedBatches, x.QueueCapacityAppliedAt, x.QueueCapacityFailure })
            .SingleOrDefaultAsync(token);
        var rows = await db.QueueCapacitySettings.FromSqlInterpolated($"SELECT * FROM [agent].[QueueCapacitySettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}")
            .AsNoTracking().ToListAsync(token);
        var desired = AgentQueueCapacityConfiguration.ToView(deviceId, rows).Effective;
        if (!device.Eligible) return new(deviceId, AgentSettingsApplyState.Disabled, desired, null, null, null, null);
        if (agent is null) return new(deviceId, AgentSettingsApplyState.NotRegistered, desired, null, null, null, null);
        var matching = agent.QueueReportEpoch is not null && agent.QueueReportSequence is not null &&
            agent.QueueCapacityReportEpoch == agent.QueueReportEpoch && agent.QueueCapacityReportSequence == agent.QueueReportSequence;
        var reportedAt = matching ? agent.QueueCapacityReportedAt : null;
        var failure = matching ? agent.QueueCapacityFailure : null;
        AgentQueueCapacitySnapshot? applied = matching && agent.QueueCapacityAppliedRevision is { } revision &&
            agent.QueueCapacityAppliedBytes is { } bytes && agent.QueueCapacityAppliedBatches is { } batches
                ? new(AgentQueueCapacitySnapshot.CurrentSchema, revision, new(bytes, batches)) : null;
        if (applied is not null && !applied.IsValid()) applied = null;
        var now = clock.GetUtcNow();
        var basis = agent.LastHeartbeatAt ?? agent.CreatedAt;
        var offlineAfter = Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var state = basis > now ? AgentSettingsApplyState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.Offline
            : agent.LastHeartbeatAt is null ? AgentSettingsApplyState.WaitingForHeartbeat
            : reportedAt is null ? AgentSettingsApplyState.WaitingForReport
            : reportedAt > now ? AgentSettingsApplyState.ClockUncertain
            : now - reportedAt >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.ReportStale
            : failure is not null ? AgentSettingsApplyState.Failed
            : applied is null ? AgentSettingsApplyState.WaitingForReport
            : string.Equals(applied.Revision, desired.Revision, StringComparison.Ordinal) ? AgentSettingsApplyState.Applied
            : AgentSettingsApplyState.Pending;
        var result = new AgentQueueCapacityStatus(deviceId, state, desired, applied, matching ? agent.QueueCapacityAppliedAt : null, reportedAt, failure);
        await transaction.CommitAsync(token);
        return result;
    }
}
