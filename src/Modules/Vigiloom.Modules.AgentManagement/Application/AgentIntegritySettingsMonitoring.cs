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
    IDeviceAgentSource devices, IIntegritySettingsReader reader, TimeProvider clock, IHostSecurityObservationReader? remote = null,
    Func<Guid, CancellationToken, Task<IntegritySettingsSnapshot>>? remoteDesired = null)
{
    public async Task<IntegritySettingsStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var observation = remote is null ? null : await remote.GetAsync(deviceId, token);
        var device = remote is null ? await devices.GetAsync(deviceId, token) : observation?.Device;
        if (device is null) return null;
        SecurityAgentObservation? agent;
        IntegritySettingsSnapshot desired;
        if (remote is not null && remoteDesired is not null)
        {
            agent = observation!.Agent;
            desired = await remoteDesired(deviceId, token);
        }
        else
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            agent = remote is not null ? observation!.Agent : await db.Agents.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new SecurityAgentObservation(x.Id, x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds,
                x.QueueReportEpoch, x.QueueReportSequence, x.InventoryConfigurationEpoch, x.InventoryConfigurationSequence,
                x.InventoryConfigurationReportedAt, x.InventoryConfigurationJson, x.IntegrityConfigurationEpoch, x.IntegrityConfigurationSequence,
                x.IntegrityConfigurationReportedAt, x.IntegrityConfigurationJson, x.BaselineConfigurationReportedAt, x.BaselineConfigurationJson))
            .SingleOrDefaultAsync(token);
            desired = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), deviceId, token);
            await transaction.CommitAsync(token);
        }
        return Evaluate(deviceId, device, agent, desired, clock.GetUtcNow());
    }

    public static IntegritySettingsStatus Evaluate(Guid deviceId, DeviceAgentState device, SecurityAgentObservation? agent,
        IntegritySettingsSnapshot desired, DateTimeOffset now)
    {
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
        return new(deviceId, state, desired.Revision, desired.Generation, report?.AppliedRevision, report?.AppliedGeneration,
            report?.AppliedAt, reportedAt, report?.Failure);

        IntegritySettingsStatus Empty(AgentSettingsApplyState state) => new(deviceId, state, desired.Revision, desired.Generation, null, null, null, null, null);
    }
}
