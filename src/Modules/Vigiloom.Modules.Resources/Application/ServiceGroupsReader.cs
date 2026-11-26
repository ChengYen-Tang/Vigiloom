using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Provides the groups associated with an application service to other services.</summary>
public sealed class ServiceGroupsReader(IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<IReadOnlyList<ResourceGroupSnapshot>> ReadAsync(Guid serviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await ServiceGroupSource.Query(db, serviceId).ToArrayAsync(token);
    }
}
