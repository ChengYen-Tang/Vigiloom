using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class CollectorSchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task CollectorAdministration_UsesVersionsAndRollsBackAuditFailures() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow;
            var monitor = new ServiceSource("monitor", new byte[32], now);
            var business = new ServiceSource("business", new byte[32], now);
            var other = new ServiceSource("other", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(monitor.Id, "collector", "monitoring", "Collector", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.AddRange(monitor, business, other); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var actor = Guid.CreateVersion7();
            var admin = new CollectorAdministration(factory, new OperationAuditWriter());
            var failing = new CollectorAdministration(factory, new FailingAudit());
            await Assert.ThrowsAsync<ArgumentException>(() => admin.RegisterAsync("collector", service.Id, false, actor, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RegisterAsync("collector", service.Id, true, actor, token));
            Assert.IsEmpty(await admin.ListAsync(1, token));
            var registered = await admin.RegisterAsync("collector", service.Id, true, actor, token);
            Assert.AreEqual(CollectorChangeStatus.Updated, registered.Status);
            var id = registered.CollectorId!.Value;
            Assert.AreEqual(CollectorChangeStatus.Conflict, (await admin.RegisterAsync("duplicate", service.Id, true, actor, token)).Status);
            var before = (await admin.ListAsync(1, token)).Single();
            Assert.AreEqual(CollectorChangeStatus.SourceUnavailable, await admin.SetRouteAsync(id, monitor.Id, before.Version, "monitor", true, actor, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SetRouteAsync(id, business.Id, before.Version, "otlp_http/a", true, actor, token));
            Assert.IsEmpty(await admin.RoutesAsync(id, token));
            Assert.AreEqual(before.Version, (await admin.ListAsync(1, token)).Single().Version);
            var races = await Task.WhenAll(admin.SetRouteAsync(id, business.Id, before.Version, "otlp_http/a", true, actor, token),
                admin.SetRouteAsync(id, other.Id, before.Version, "otlp_http/b", true, actor, token));
            Assert.HasCount(1, races.Where(x => x == CollectorChangeStatus.Updated));
            Assert.HasCount(1, races.Where(x => x == CollectorChangeStatus.Conflict));
            var route = (await admin.RoutesAsync(id, token)).Single();
            var current = (await admin.ListAsync(1, token)).Single();
            Assert.AreNotEqual(before.Version, current.Version);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SetRouteAsync(id, route.SourceId, current.Version, null, false, actor, token));
            Assert.HasCount(1, await admin.RoutesAsync(id, token));
            Assert.AreEqual(current.Version, (await admin.ListAsync(1, token)).Single().Version);
            Assert.AreEqual(CollectorChangeStatus.Updated, await admin.SetRouteAsync(id, route.SourceId, current.Version, null, false, actor, token));
            Assert.IsEmpty(await admin.RoutesAsync(id, token));
            var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var history = await new OperationAuditQueryService(new PooledDbContextFactory<OperationsDbContext>(operations)).QueryCollectorsAsync(query, token);
            Assert.HasCount(3, history.Items);
            CollectionAssert.AreEquivalent(new[] { CollectorAction.Registered, CollectorAction.RouteAdded, CollectorAction.RouteRemoved },
                history.Items.Select(x => x.Collector!.Action).ToArray());
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task CollectorMappings_EnforceIdentityAndExactExporterKeys() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var monitorA = new ServiceSource("monitor A", new byte[32], now);
            var monitorB = new ServiceSource("monitor B", new byte[32], now);
            var business = new ServiceSource("business", new byte[32], now);
            var another = new ServiceSource("another", new byte[32], now);
            var serviceA = new MonitoredService(new ServiceCandidate(monitorA.Id, "collector", "prod", "A", now), now);
            var serviceB = new MonitoredService(new ServiceCandidate(monitorB.Id, "collector", "prod", "B", now), now);
            var first = new CollectorRegistration("first", serviceA.Id, now);
            var second = new CollectorRegistration("second", serviceB.Id, now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(monitorA, monitorB, business, another);
                db.Services.AddRange(serviceA, serviceB);
                db.Collectors.AddRange(first, second);
                db.CollectorRoutes.AddRange(new CollectorRoute(first.Id, business.Id, "otlp_http/a"),
                    new CollectorRoute(first.Id, another.Id, "otlp_http/a "), new CollectorRoute(second.Id, business.Id, "otlp_http/a"));
                await db.SaveChangesAsync(token);
                Assert.AreEqual(7, first.Id.Version);
                Assert.HasCount(8, first.Version);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(3, await db.CollectorRoutes.CountAsync(token));
                db.Collectors.Add(new CollectorRegistration("duplicate", serviceA.Id, now));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.CollectorRoutes.Add(new CollectorRoute(second.Id, another.Id, "otlp_http/a"));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.CollectorRoutes.Add(new CollectorRoute(Guid.CreateVersion7(), another.Id, "missing"));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Collectors.Remove(await db.Collectors.SingleAsync(x => x.Id == first.Id, token));
                await db.SaveChangesAsync(token);
                Assert.AreEqual(1, await db.CollectorRoutes.CountAsync(token));
                Assert.AreEqual(2, await db.Services.CountAsync(token));
            }
        }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit entry, CancellationToken token)
        {
            await new OperationAuditWriter().AppendAsync(connection, transaction, entry, token);
            throw new InvalidOperationException("Injected failure after audit insertion.");
        }
    }
}
