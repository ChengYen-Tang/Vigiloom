using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Compares the desired inventory policy to the latest worker-observed policy.</summary>
public sealed class AgentInventorySettingsMonitoring(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, IInventorySettingsReader reader, TimeProvider clock, IHostSecurityObservationReader? remote = null,
    Func<Guid, CancellationToken, Task<InventorySettingsSnapshot>>? remoteDesired = null)
{
    public async Task<InventorySettingsStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var observation = remote is null ? null : await remote.GetAsync(deviceId, token);
        var device = remote is null ? await devices.GetAsync(deviceId, token) : observation?.Device;
        if (device is null) return null;
        SecurityAgentObservation? agent;
        InventorySettingsSnapshot desired;
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
        if (!device.Eligible) return new(deviceId, AgentSettingsApplyState.Disabled, desired, null, null, null, null);
        if (agent is null) return new(deviceId, AgentSettingsApplyState.NotRegistered, desired, null, null, null, null);
        var matching = agent.QueueReportEpoch is not null && agent.QueueReportSequence is not null &&
            agent.InventoryConfigurationEpoch == agent.QueueReportEpoch && agent.InventoryConfigurationSequence == agent.QueueReportSequence;
        InventorySettingsReport? report = null;
        if (matching && agent.InventoryConfigurationJson is { } json)
        {
            try { report = JsonSerializer.Deserialize<InventorySettingsReport>(json); }
            catch (JsonException) { }
            if (report is not null && !report.IsValid()) report = null;
        }
        var reportedAt = report is null ? null : agent.InventoryConfigurationReportedAt;
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
            : report.Applied is null ? AgentSettingsApplyState.WaitingForReport
            : report.Applied.Revision == desired.Revision ? AgentSettingsApplyState.Applied : AgentSettingsApplyState.Pending;
        return new(deviceId, state, desired, report?.Applied, report?.AppliedAt, reportedAt, report?.Failure);
    }
}
