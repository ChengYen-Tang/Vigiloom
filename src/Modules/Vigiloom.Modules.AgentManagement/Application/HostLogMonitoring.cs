using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed record HostLogStatusView(DateTimeOffset ReceivedAt, int OfflineAfterSeconds, AgentLogStatusReport Report);

/// <summary>Reads the last non-secret source observations for the current Agent session.</summary>
public sealed class HostLogMonitoring(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices)
{
    public async Task<HostLogStatusView?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is not { Eligible: true }) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new { x.QueueReportEpoch, x.QueueReportSequence, x.LogStatusReportEpoch, x.LogStatusReportSequence,
                x.LogStatusReportedAt, x.LogStatusJson, x.HeartbeatSafetyIntervalSeconds }).SingleOrDefaultAsync(token);
        if (agent is null || agent.LogStatusReportEpoch is null || agent.LogStatusReportEpoch != agent.QueueReportEpoch ||
            agent.LogStatusReportSequence != agent.QueueReportSequence || agent.LogStatusReportedAt is not { } received ||
            agent.LogStatusJson is not { Length: > 0 and <= 8192 } json) return null;
        try
        {
            var report = JsonSerializer.Deserialize<AgentLogStatusReport>(json);
            return report?.IsValid() == true ? new(received, Math.Max(AgentMonitoring.OfflineAfterSeconds, agent.HeartbeatSafetyIntervalSeconds * 3), report) : null;
        }
        catch (JsonException) { return null; }
    }
}
