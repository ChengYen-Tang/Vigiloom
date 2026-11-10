using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.SqlClient;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricWindowRevisionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ConcurrentWritesToSameWindowDoNotLoseARevision() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddHours(-1));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source);
                setup.Services.Add(service);
                await setup.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var metric = new Metric { Name = "gauge", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000, AsInt = 1 } } } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingestion = new MetricIngestion(factory);
            var results = await Task.WhenAll(ingestion.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token),
                ingestion.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            Assert.IsTrue(results.All(x => x == MetricIngestionStatus.Accepted));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(2L, (await check.MetricRollupWindows.SingleAsync(token)).InputRevision);
            Assert.AreEqual(2, await check.MetricPoints.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MetricBatchAtomicallyTracksWindowsAndRejectsPrunedHistory() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var boundary = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300);
            var nanos = (ulong)boundary.ToUnixTimeSeconds() * 1_000_000_000;
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddHours(-1));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source);
                setup.Services.Add(service);
                await setup.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var scope = new ScopeMetrics();
            scope.Metrics.Add(new Metric { Name = "gauge", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = nanos, AsInt = 1 },
                new NumberDataPoint { TimeUnixNano = nanos + 1, AsInt = 2 } } } });
            scope.Metrics.Add(new Metric { Name = "counter", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta,
                DataPoints = { new NumberDataPoint { StartTimeUnixNano = nanos - 1, TimeUnixNano = nanos, AsInt = 1 },
                    new NumberDataPoint { StartTimeUnixNano = nanos, TimeUnixNano = nanos + 1, AsInt = 1 } } } });
            resource.ScopeMetrics.Add(scope);
            var request = new ExportMetricsServiceRequest { ResourceMetrics = { resource } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new MetricExport(factory, new CandidateDiscovery(factory));
            Assert.AreEqual(ExportStatus.Complete, (await export.WriteAsync(source.Id, request, token)).Status);
            await using (var projected = new ServiceObservabilityDbContext(options))
            {
                foreach (var window in await projected.MetricRollupWindows.ToArrayAsync(token))
                {
                    window.ProjectedRevision = window.InputRevision;
                    window.ProjectionVersion = 1;
                    window.ProjectedAt = now;
                    window.IsComplete = true;
                }
                await projected.SaveChangesAsync(token);
            }
            Assert.AreEqual(ExportStatus.Complete, (await export.WriteAsync(source.Id, request, token)).Status);
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                var windows = await verify.MetricRollupWindows.OrderBy(x => x.WindowStart).ToArrayAsync(token);
                Assert.HasCount(2, windows);
                Assert.AreEqual(boundary.AddMinutes(-5), windows[0].WindowStart);
                Assert.AreEqual(boundary, windows[1].WindowStart);
                Assert.IsTrue(windows.All(x => x.InputRevision == 2));
                Assert.IsTrue(windows.All(x => x.ProjectedRevision == 1));
                Assert.AreEqual(8, await verify.MetricPoints.CountAsync(token));
                windows[0].ProjectedRevision = 2;
                windows[0].ProjectionVersion = 1;
                windows[0].ProjectedAt = now;
                windows[0].IsComplete = true;
                windows[0].RawPruned = true;
                await verify.SaveChangesAsync(token);
            }
            var rejected = await export.WriteAsync(source.Id, request, token);
            Assert.AreEqual(ExportStatus.Partial, rejected.Status);
            Assert.AreEqual(4L, rejected.Rejected);
            Assert.IsTrue(rejected.MetricWindowPruned);
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(8, await check.MetricPoints.CountAsync(token));
                Assert.IsTrue((await check.MetricRollupWindows.ToArrayAsync(token)).All(x => x.InputRevision == 2));
                await check.Database.ExecuteSqlRawAsync("ALTER TABLE [service].[MetricPoints] WITH NOCHECK ADD CONSTRAINT [CK_TestMetricRevisionFailure] CHECK ([Kind] <> 1)", token);
                var old = await check.MetricRollupWindows.SingleAsync(x => x.WindowStart == boundary.AddMinutes(-5), token);
                old.RawPruned = false;
                await check.SaveChangesAsync(token);
            }
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => export.WriteAsync(source.Id, request, token));
            var sql = Assert.IsInstanceOfType<SqlException>(failure.InnerException);
            Assert.AreEqual(547, sql.Number);
            Assert.Contains("CK_TestMetricRevisionFailure", sql.Message);
            await using var final = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(8, await final.MetricPoints.CountAsync(token));
            Assert.IsTrue((await final.MetricRollupWindows.ToArrayAsync(token)).All(x => x.InputRevision == 2));
        }, context.CancellationToken);
}
