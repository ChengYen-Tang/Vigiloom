using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Compares desired integrity policy with the latest ordered worker report.</summary>
public sealed class AgentIntegritySettingsMonitoring(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, IIntegritySettingsReader reader, TimeProvider clock)
{
    public async Task<IntegritySettingsStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.QueueReportEpoch, x.QueueReportSequence,
                x.IntegrityConfigurationEpoch, x.IntegrityConfigurationSequence, x.IntegrityConfigurationReportedAt, x.IntegrityConfigurationJson })
            .SingleOrDefaultAsync(token);
        var desired = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), deviceId, token);
        if (!device.Eligible) return Empty(AgentSettingsApplyState.Disabled);
        if (agent is null) return Empty(AgentSettingsApplyState.NotRegistered);
        var matching = agent.QueueReportEpoch is not null && agent.QueueReportSequence is not null &&
            agent.IntegrityConfigurationEpoch == agent.QueueReportEpoch && agent.IntegrityConfigurationSequence == agent.QueueReportSequence;
        IntegritySettingsReport? report = null;
        if (matching && agent.IntegrityConfigurationJson is { } json)
        {
            try { report = JsonSerializer.Deserialize<IntegritySettingsReport>(json); }
            catch (JsonException) { }
            if (report is not null && !report.IsValid()) report = null;
        }
        var reportedAt = report is null ? null : agent.IntegrityConfigurationReportedAt;
        var now = clock.GetUtcNow();
        var basis = agent.LastHeartbeatAt ?? agent.CreatedAt;
        var offlineAfter = Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3);
        var state = basis > now ? AgentSettingsApplyState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.Offline
            : agent.LastHeartbeatAt is null ? AgentSettingsApplyState.WaitingForHeartbeat
            : reportedAt is null ? AgentSettingsApplyState.WaitingForReport
            : reportedAt > now ? AgentSettingsApplyState.ClockUncertain
            : now - reportedAt >= TimeSpan.FromSeconds(offlineAfter) ? AgentSettingsApplyState.ReportStale
            : report!.Failure is not null ? AgentSettingsApplyState.Failed
            : report.AppliedRevision is null ? AgentSettingsApplyState.WaitingForReport
            : report.AppliedRevision == desired.Revision && report.AppliedGeneration == desired.Generation ? AgentSettingsApplyState.Applied : AgentSettingsApplyState.Pending;
        await transaction.CommitAsync(token);
        return new(deviceId, state, desired.Revision, desired.Generation, report?.AppliedRevision, report?.AppliedGeneration,
            report?.AppliedAt, reportedAt, report?.Failure);

        IntegritySettingsStatus Empty(AgentSettingsApplyState state) => new(deviceId, state, desired.Revision, desired.Generation, null, null, null, null, null);
    }
}
