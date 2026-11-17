using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class GroupAgentSource : IGroupAgentSource
{
    public async Task<IReadOnlyList<GroupAgentDevice>?> ReadAsync(Guid groupId, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<ResourcesDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using var db = new ResourcesDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        // Membership changes lock the group first. Retaining a shared group lock makes the snapshot consistent.
        var group = await db.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (HOLDLOCK) WHERE [Id] = {groupId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (group is null) return null;
        return await (from membership in db.DeviceGroups.AsNoTracking()
            join device in db.Devices.AsNoTracking() on membership.DeviceId equals device.Id
            where membership.GroupId == groupId && device.DeepMonitoring && device.IsEnabled && !device.IsArchived
            orderby device.RowId
            select new GroupAgentDevice(device.Id, device.AgentAuthorizationGeneration, device.Name)).ToArrayAsync(token);
    }
}
