using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicationMigrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServicePublicationPeriod_UpgradesExistingRowsAndRejectsLegacyWrites() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
        var oldTime = DateTimeOffset.UtcNow.AddDays(-1);
        var source = new ServiceSource("source", new byte[32], oldTime);
        var publicId = Guid.CreateVersion7();
        var privateId = Guid.CreateVersion7();
        await using var db = new ServiceObservabilityDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261113160900_ServicePublication", token);
        db.Sources.Add(source);
        await db.SaveChangesAsync(token);
        foreach (var (id, name, isPublic) in new[] { (publicId, "public", true), (privateId, "private", false) })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [service].[Services] ([Id], [SourceId], [ServiceKey], [ServiceNamespace], [Environment], [DisplayName], [ApprovedAt], [CreatedAt], [UpdatedAt], [IsPublic], [PublicDisplayName], [PublicPolicyVersion])
                VALUES ({id}, {source.Id}, {name}, {""}, {"production"}, {name}, {oldTime}, {oldTime}, {oldTime}, {isPublic}, {name}, {PublicationPolicy.BaselineVersion})
                """, token);
        }
        var oldGuard = await Assert.ThrowsAsync<SqlException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261110212007_MetricRollupHistogramModes", token));
        Assert.AreEqual(51003, oldGuard.Number);
        await db.Database.MigrateAsync(token);
        var rows = await db.Services.AsNoTracking().Where(x => x.Id == publicId || x.Id == privateId).ToListAsync(token);
        var existing = rows.Single(x => x.Id == publicId);
        Assert.IsNotNull(existing.PublicSince);
        Assert.AreEqual(MonitoredService.PendingPublicSince, existing.PublicSince.Value);
        Assert.IsNull(rows.Single(x => x.Id == privateId).PublicSince);
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [service].[Services] SET [IsPublic] = 1 WHERE [Id] = {privateId}", token));
        Assert.AreEqual(547, failure.Number);
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Downgrade_PreservesConfiguredPublication() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var resourceOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var serviceOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
        var now = DateTimeOffset.UtcNow;
        await using (var resources = new ResourcesDbContext(resourceOptions))
        {
            await resources.GetService<IMigrator>().MigrateAsync("20261113160848_ResourcePublication", token);
            var id = Guid.CreateVersion7();
            await resources.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [resources].[Devices] ([Id], [Name], [Address], [HasAgent], [IsArchived], [CreatedAt], [UpdatedAt], [IsPublic], [PublicDisplayName], [PublicPolicyVersion])
                VALUES ({id}, {"router"}, {"10.1.2.3"}, {false}, {false}, {now}, {now}, {true}, {"Public router"}, {PublicationPolicy.BaselineVersion})
                """, token);
            var failure = await Assert.ThrowsAsync<SqlException>(() => resources.GetService<IMigrator>().MigrateAsync("20261103233336_GroupMembership", token));
            Assert.AreEqual(51001, failure.Number);
        }
        await using (var resources = new ResourcesDbContext(resourceOptions))
            Assert.AreEqual(1, await resources.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [resources].[Devices] WHERE [IsPublic] = 1 AND [PublicDisplayName] = N'Public router'").SingleAsync(token));

        await using (var services = new ServiceObservabilityDbContext(serviceOptions))
        {
            await services.Database.MigrateAsync(token);
            var source = new ServiceSource("source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "key", "environment", "Service", now);
            var service = new MonitoredService(candidate, now);
            service.SetPublication(true, "Public service", PublicationPolicy.BaselineVersion, now);
            services.Sources.Add(source); services.Candidates.Add(candidate); services.Services.Add(service);
            await services.SaveChangesAsync(token);
            var failure = await Assert.ThrowsAsync<SqlException>(() => services.GetService<IMigrator>().MigrateAsync("20261110212007_MetricRollupHistogramModes", token));
            Assert.AreEqual(51006, failure.Number);
        }
        await using (var services = new ServiceObservabilityDbContext(serviceOptions))
            Assert.IsTrue(await services.Services.AnyAsync(x => x.IsPublic && x.PublicDisplayName == "Public service", token));
    }, context.CancellationToken);
}
