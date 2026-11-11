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
    IDeviceAgentSource devices, IInventorySettingsReader reader, TimeProvider clock)
{
    public async Task<InventorySettingsStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.CreatedAt, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds, x.QueueReportEpoch, x.QueueReportSequence,
                x.InventoryConfigurationEpoch, x.InventoryConfigurationSequence, x.InventoryConfigurationReportedAt, x.InventoryConfigurationJson })
            .SingleOrDefaultAsync(token);
        var desired = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), deviceId, token);
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
        await transaction.CommitAsync(token);
        return new(deviceId, state, desired, report?.Applied, report?.AppliedAt, reportedAt, report?.Failure);
    }
}
