using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricSchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MetricNameLookup_UpgradePreservesHistoricalPayload() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "collector", "prod", "Collector", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261104045143_SourceCredentialVersion", token);
                // Seed only columns available before the metric-name migration.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Sources] ([Id],[Name],[Verifier],[CreatedAt],[UpdatedAt]) VALUES ({source.Id},{source.Name},{source.Verifier},{now},{now})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Services] ([Id],[SourceId],[CandidateId],[ServiceKey],[ServiceNamespace],[Environment],[DisplayName],[ApprovedAt],[CreatedAt],[UpdatedAt],[IsPublic]) VALUES ({service.Id},{service.SourceId},{service.CandidateId},{service.ServiceKey},{service.ServiceNamespace},{service.Environment},{service.DisplayName},{service.ApprovedAt},{service.CreatedAt},{service.UpdatedAt},{false})", token);
                var payload = new byte[] { 1, 2, 3 };
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[MetricContexts] ([ServiceId],[ResourcePayload],[ScopePayload],[MetricPayload],[ResourceSchemaUrl],[ScopeSchemaUrl]) VALUES ({service.Id},{payload},{payload},{payload},{""},{""})", token);
                await db.Database.MigrateAsync(token);
            }
            await using var check = new ServiceObservabilityDbContext(options);
            var saved = await check.MetricContexts.SingleAsync(token);
            Assert.IsNull(saved.MetricName);
            Assert.IsNull(saved.MetricNameBytes);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, saved.MetricPayload);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Metrics_PreservePayloadAndRejectCrossServiceContextAndUnsafeRollback() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var first = new MonitoredService(new ServiceCandidate(source.Id, "one", "prod", "One", now), now);
            var second = new MonitoredService(new ServiceCandidate(source.Id, "two", "prod", "Two", now), now);
            long contextId;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.AddRange(first, second);
                var shared = new MetricContext { ServiceId = first.Id, ResourcePayload = [1], ScopePayload = [2], MetricPayload = [3], ResourceSchemaUrl = "r", ScopeSchemaUrl = "s" };
                db.MetricPoints.Add(new MetricPoint { ServiceId = first.Id, Context = shared, OccurredAt = now, ReceivedAt = now, Kind = 1, PointPayload = [4] });
                await db.SaveChangesAsync(token);
                contextId = shared.RowId;
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var saved = await db.MetricPoints.Include(x => x.Context).SingleAsync(token);
                CollectionAssert.AreEqual(new byte[] { 3 }, saved.Context.MetricPayload);
                CollectionAssert.AreEqual(new byte[] { 4 }, saved.PointPayload);
                db.MetricPoints.Add(new MetricPoint { ServiceId = second.Id, ContextId = contextId, OccurredAt = now, ReceivedAt = now, Kind = 1, PointPayload = [4] });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            var history = new MetricHistory(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            Assert.HasCount(1, (await history.QueryAsync(first.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token)).Items);
            Assert.IsEmpty((await history.QueryAsync(second.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token)).Items);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.QueryAsync(first.Id, now.AddDays(-32), now, 1, token));
            await using (var db = new ServiceObservabilityDbContext(options))
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261104031155_TelemetryTraces", token));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await check.MetricPoints.CountAsync(token));
        }, context.CancellationToken);
}
