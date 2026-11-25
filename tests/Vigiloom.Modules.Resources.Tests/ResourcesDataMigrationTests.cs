using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;

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
            var telemetryOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(telemetryOptions)) await db.Database.MigrateAsync(token);
            var agentOptions = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
            await using (var db = new AgentManagementDbContext(agentOptions)) await db.Database.MigrateAsync(token);
            var package = new Vigiloom.Modules.AgentManagement.Domain.AgentPackage
            {
                Id = Guid.CreateVersion7(), Version = "1.2.3", RuntimeIdentifier = "win-x64",
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Size = 1024
            };
            await using (var db = new AgentManagementDbContext(agentOptions))
            {
                db.Packages.Add(package);
                await db.SaveChangesAsync(token);
            }
            var now = DateTimeOffset.UtcNow;
            var telemetrySource = new ServiceSource("Source", new byte[32], now);
            var candidate = new ServiceCandidate(telemetrySource.Id, "new-api", "Production", "New API", now);
            var approved = new MonitoredService(new ServiceCandidate(telemetrySource.Id, "order-api", "Production", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(telemetryOptions))
            {
                db.Sources.Add(telemetrySource);
                db.Candidates.Add(candidate);
                db.Services.Add(approved);
                await db.SaveChangesAsync(token);
            }
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
                Assert.IsTrue(await db.ApplicationCandidates.AnyAsync(x => x.Id == candidate.Id && x.SourceId == telemetrySource.Id, token));
                Assert.IsTrue(await db.ApplicationServices.AnyAsync(x => x.Id == approved.Id && x.DisplayName == "Orders", token));
                Assert.IsTrue(await db.AgentPackages.AnyAsync(x => x.Id == package.Id && x.Version == package.Version && x.Sha256 == package.Sha256, token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => ResourcesDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), TestContext.CancellationToken);

    private static DbContextOptions<ResourcesDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
}
