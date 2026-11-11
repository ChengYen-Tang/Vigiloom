using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves inventory ownership without using a hostname or network address.</summary>
public sealed class DeviceInventoryAgent(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices)
{
    public async Task<(bool Exists, Guid? AgentId)> ResolveAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return (false, null);
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation)
            .OrderBy(x => x.RevokedAt != null).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.RowId)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        return (true, agent);
    }
}
