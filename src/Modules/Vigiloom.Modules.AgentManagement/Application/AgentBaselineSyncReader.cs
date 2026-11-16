using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads the active Agent's latest ordered baseline settings report.</summary>
public sealed class AgentBaselineSyncReader(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices) : IAgentBaselineSyncReader
{
    public async Task<AgentBaselineSyncObservation?> GetAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await devices.GetAsync(deviceId, cancellationToken);
        if (device is not { Eligible: true }) return null;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.Id, x.LastHeartbeatAt, x.HeartbeatSafetyIntervalSeconds,
                x.BaselineConfigurationJson, x.BaselineConfigurationReportedAt }).SingleOrDefaultAsync(cancellationToken);
        if (agent is null) return null;
        BaselineSettingsReport? report = null;
        if (agent.BaselineConfigurationJson is { } json)
        {
            try { report = JsonSerializer.Deserialize<BaselineSettingsReport>(json); }
            catch (JsonException) { }
            if (report is not null && !report.IsValid()) report = null;
        }
        return new(agent.Id, agent.LastHeartbeatAt,
            Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3),
            report is null ? null : agent.BaselineConfigurationReportedAt, report);
    }
}
