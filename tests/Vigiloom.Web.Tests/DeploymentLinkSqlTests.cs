using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeploymentLinkSqlTests(TestContext context)
{
    [TestMethod]
    public Task Migration_RejectsRemovingRecordedLinks() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var services = host.Services;
        await using (var db = await services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using var links = await services.GetRequiredService<IDbContextFactory<DeploymentLinksDbContext>>().CreateDbContextAsync(token);
        var migrator = links.GetService<IMigrator>();
        await links.Database.MigrateAsync(token);
        await migrator.MigrateAsync("0", token);
        await links.Database.MigrateAsync(token);
        var now = DateTimeOffset.UtcNow;
        var device = new Device("migration", "127.0.0.1", false, now);
        var source = new ServiceSource("migration", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "service", "test", "Service", now);
        var service = new MonitoredService(candidate, now);
        var instanceId = Guid.CreateVersion7();
        await using (var db = await services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        await using (var db = await services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service); await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"node"},{now},{now})", token);
        }
        links.Links.Add(new DeploymentLink(device.Id, instanceId, Guid.CreateVersion7(), "Verified", now));
        await links.SaveChangesAsync(token);
        var failure = await Assert.ThrowsExactlyAsync<SqlException>(() => migrator.MigrateAsync("0", token));
        Assert.AreEqual(51000, failure.Number);
        Assert.AreEqual(1, await links.Links.CountAsync(token));
        await links.Links.ExecuteDeleteAsync(token);
        await migrator.MigrateAsync("0", token);
        await links.Database.MigrateAsync(token);
        Assert.AreEqual(0, await links.Links.CountAsync(token));
    }, context.CancellationToken);

    [TestMethod]
    public async Task Relationships_EnforceBothParentsAndOneActivePeriod()
    {
        await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var resourceFactory = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var serviceFactory = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var linkFactory = host.Services.GetRequiredService<IDbContextFactory<DeploymentLinksDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var device = new Device("server", "127.0.0.1", true, now);
        var source = new ServiceSource("source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "production", "Orders", now);
        var service = new MonitoredService(candidate, now);
        var instanceId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        await using (var db = await resourceFactory.CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        await using (var db = await serviceFactory.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"node"},{now},{now})", token);
        }

        async Task<int> InsertAsync(Guid deviceId, Guid serviceInstanceId, DateTimeOffset? startedAt = null)
        {
            await using var db = await linkFactory.CreateDbContextAsync(token);
            db.Links.Add(new DeploymentLink(deviceId, serviceInstanceId, actorId, "Administrator verified deployment", startedAt ?? now));
            try { await db.SaveChangesAsync(token); return 0; }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sql) { return sql.Number; }
        }

        Assert.AreEqual(547, await InsertAsync(Guid.CreateVersion7(), instanceId));
        Assert.AreEqual(547, await InsertAsync(device.Id, Guid.CreateVersion7()));
        Assert.AreEqual(0, await InsertAsync(device.Id, instanceId));
        Assert.Contains(await InsertAsync(device.Id, instanceId), new[] { 2601, 2627 });

        await using (var db = await linkFactory.CreateDbContextAsync(token))
        {
            var first = await db.Links.SingleAsync(token);
            first.End(now.AddSeconds(1));
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(0, await InsertAsync(device.Id, instanceId, now.AddSeconds(2)));
        await using (var db = await linkFactory.CreateDbContextAsync(token))
            Assert.AreEqual(2, await db.Links.CountAsync(token));
        }, context.CancellationToken);
        await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<DeploymentLinksDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(0, await db.Links.CountAsync(token));
            Assert.IsFalse((await db.Database.GetPendingMigrationsAsync(token)).Any());
        }, context.CancellationToken);
    }
}
