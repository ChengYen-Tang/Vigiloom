using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class ServiceGroupSource : IServiceGroupSource
{
    public async Task<IReadOnlyList<ResourceGroupSnapshot>> ReadAsync(Guid serviceId, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<ResourcesDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using var db = new ResourcesDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return await Query(db, serviceId).ToArrayAsync(token);
    }

    internal static IQueryable<ResourceGroupSnapshot> Query(ResourcesDbContext db, Guid serviceId) =>
                     from member in db.ServiceGroups.AsNoTracking()
                      join item in db.Groups.AsNoTracking() on member.GroupId equals item.Id
                      where member.ServiceId == serviceId
                      orderby item.Name
                      select new ResourceGroupSnapshot(item.Id, item.Name);
}
