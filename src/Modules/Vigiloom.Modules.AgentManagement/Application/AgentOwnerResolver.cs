using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves only a non-revoked Agent belonging to the device's current generation.</summary>
public sealed class AgentOwnerResolver(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices) : IAgentOwnerResolver
{
    public async Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId.Version != 7) throw new ArgumentException("Invalid device identity.", nameof(deviceId));
        var device = await devices.GetAsync(deviceId, token);
        if (device is not { Eligible: true }) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null)
            .Select(x => new InventoryOwner(x.Id, x.DeviceId, x.DeviceGeneration)).SingleOrDefaultAsync(token);
    }
}
