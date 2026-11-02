using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
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
            .Select(x => new { x.CreatedAt, x.LastHeartbeatAt }).SingleOrDefaultAsync(token);
        if (agent is null) return new(deviceId, AgentConnectionState.NotRegistered, null, OfflineAfterSeconds);
        var now = clock.GetUtcNow();
        var last = agent.LastHeartbeatAt;
        var basis = last ?? agent.CreatedAt;
        var state = basis > now ? AgentConnectionState.ClockUncertain
            : now - basis >= TimeSpan.FromSeconds(OfflineAfterSeconds) ? AgentConnectionState.Offline
            : last is null ? AgentConnectionState.AwaitingFirstHeartbeat : AgentConnectionState.Online;
        return new(deviceId, state, last, OfflineAfterSeconds);
    }
}
