using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ResourcesDataMigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Copy_PreservesDevicesAndGroupMemberships() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var sourceOptions = Options(source.ConnectionString);
            var targetOptions = Options(target.ConnectionString);
            await using (var db = new ResourcesDbContext(sourceOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new ResourcesDbContext(targetOptions)) await db.Database.MigrateAsync(token);
            var device = new Device("Server", "server.example", true, DateTimeOffset.UtcNow);
            var group = new Group("Servers", DateTimeOffset.UtcNow);
            var serviceId = Guid.CreateVersion7();
            await using (var db = new ResourcesDbContext(sourceOptions))
            {
                db.Devices.Add(device);
                db.Groups.Add(group);
                db.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
                db.ServiceGroups.Add(new ServiceGroup(group.Id, serviceId));
                db.DeviceTargets.Add(new DeviceTarget(device.Id, "extra.example"));
                await db.SaveChangesAsync(token);
            }
            await ResourcesDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using (var db = new ResourcesDbContext(targetOptions))
            {
                Assert.IsTrue(await db.Devices.AnyAsync(x => x.Id == device.Id && x.Address == "server.example", token));
                Assert.IsTrue(await db.DeviceTargets.AnyAsync(x => x.DeviceId == device.Id && x.Address == "extra.example", token));
                Assert.IsTrue(await db.DeviceGroups.AnyAsync(x => x.DeviceId == device.Id && x.GroupId == group.Id, token));
                Assert.IsTrue(await db.ServiceGroups.AnyAsync(x => x.ServiceId == serviceId && x.GroupId == group.Id, token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => ResourcesDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), TestContext.CancellationToken);

    private static DbContextOptions<ResourcesDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
}
