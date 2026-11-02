using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reports desired and observed transport settings independently of Ping.</summary>
public sealed class AgentSettingsMonitoring(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<AgentSettingsStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.ConfigurationReportedAt,
                x.AppliedConfigurationRevision, x.AppliedHeartbeatSeconds, x.AppliedSettingsPollSeconds, x.AppliedBatchIntervalSeconds,
                x.ConfigurationAppliedAt, x.ConfigurationFailure })
            .SingleOrDefaultAsync(token);
        var rows = await db.Settings.FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}")
            .AsNoTracking().ToListAsync(token);
        var desired = AgentConfiguration.ToView(deviceId, rows).Effective;
        if (!device.Eligible) return new(deviceId, AgentSettingsApplyState.Disabled, desired, null, null, null, null);
        if (agent is null) return new(deviceId, AgentSettingsApplyState.NotRegistered, desired, null, null, null, null);
        var now = clock.GetUtcNow();
        var basis = agent.LastHeartbeatAt ?? agent.CreatedAt;
        var offlineAfter = Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var state = basis > now ? AgentSettingsApplyState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.Offline
            : agent.LastHeartbeatAt is null ? AgentSettingsApplyState.WaitingForHeartbeat
            : agent.ConfigurationReportedAt is null ? AgentSettingsApplyState.WaitingForReport
            : agent.ConfigurationReportedAt > now ? AgentSettingsApplyState.ClockUncertain
            : now - agent.ConfigurationReportedAt >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.ReportStale
            : agent.ConfigurationFailure is not null ? AgentSettingsApplyState.Failed
            : string.Equals(agent.AppliedConfigurationRevision, desired.Revision, StringComparison.Ordinal) ? AgentSettingsApplyState.Applied
            : AgentSettingsApplyState.Pending;
        AgentSettingsSnapshot? applied = agent.AppliedConfigurationRevision is { } revision && agent.AppliedHeartbeatSeconds is { } heartbeat &&
            agent.AppliedSettingsPollSeconds is { } poll && agent.AppliedBatchIntervalSeconds is { } batch
            ? new AgentSettingsSnapshot(AgentSettingsSnapshot.CurrentSchema, revision, new AgentSettings(heartbeat, poll, batch)) : null;
        if (applied is not null && !applied.IsValid()) applied = null;
        if (state == AgentSettingsApplyState.Applied && applied is null) state = AgentSettingsApplyState.WaitingForReport;
        var result = new AgentSettingsStatus(deviceId, state, desired, applied, agent.ConfigurationAppliedAt, agent.ConfigurationReportedAt, agent.ConfigurationFailure);
        await transaction.CommitAsync(token);
        return result;
    }
}
