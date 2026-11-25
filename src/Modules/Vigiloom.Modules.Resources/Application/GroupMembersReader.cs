using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class GroupMembersReader(IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<GroupMembers?> GetAsync(Guid groupId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Groups.AsNoTracking().AnyAsync(group => group.Id == groupId, token)) return null;
        var devices = await db.DeviceGroups.AsNoTracking().Where(member => member.GroupId == groupId)
            .Select(member => member.DeviceId).ToArrayAsync(token);
        var services = await db.ServiceGroups.AsNoTracking().Where(member => member.GroupId == groupId)
            .Select(member => member.ServiceId).ToArrayAsync(token);
        return new(groupId, devices, services);
    }
}
