using Google.Protobuf;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricRollupMaterializerTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MaterializationIsRepeatableAndRollsBackFailedReplacement() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-10);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), start.AddMinutes(-5));
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
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            var materializer = new MetricRollupMaterializer(factory);
            Metric Gauge(int seconds, long value) => new() { Name = "load", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = (ulong)start.AddSeconds(seconds).ToUnixTimeMilliseconds() * 1_000_000, AsInt = value } } } };
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(1, 3), token));
            Assert.AreEqual(MetricRollupStatus.Complete, await materializer.MaterializeAsync(service.Id, start, token));
            Assert.AreEqual(MetricRollupStatus.Current, await materializer.MaterializeAsync(service.Id, start, token));
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(2, 8), token));
            Assert.AreEqual(MetricRollupStatus.Complete, await materializer.MaterializeAsync(service.Id, start, token));
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                var window = await verify.MetricRollupWindows.SingleAsync(token);
                Assert.AreEqual(2L, window.InputRevision);
                Assert.AreEqual(window.InputRevision, window.ProjectedRevision);
                Assert.IsTrue(window.IsComplete);
                var series = await verify.MetricRollupSeries.SingleAsync(token);
                Assert.IsFalse(series.RequiresRaw);
                var result = ResourceMetrics.Parser.ParseFrom(series.ResultPayload);
                Assert.AreEqual(8L, result.ScopeMetrics.Single().Metrics.Single().Gauge.DataPoints.Single().AsInt);
            }
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(3, 12), token));
            await using (var fault = new ServiceObservabilityDbContext(options))
                await fault.Database.ExecuteSqlRawAsync("ALTER TABLE [service].[MetricRollupSeries] WITH NOCHECK ADD CONSTRAINT [CK_TestRollupFailure] CHECK ([Mode] <> 1)", token);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => materializer.MaterializeAsync(service.Id, start, token));
            var sql = Assert.IsInstanceOfType<SqlException>(failure.InnerException);
            Assert.AreEqual(547, sql.Number);
            Assert.Contains("CK_TestRollupFailure", sql.Message);
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                var window = await verify.MetricRollupWindows.SingleAsync(token);
                Assert.AreEqual(3L, window.InputRevision);
                Assert.AreEqual(2L, window.ProjectedRevision);
                var saved = await verify.MetricRollupSeries.SingleAsync(token);
                Assert.AreEqual(8L, ResourceMetrics.Parser.ParseFrom(saved.ResultPayload).ScopeMetrics.Single().Metrics.Single().Gauge.DataPoints.Single().AsInt);
            }
            await using (var legacy = new ServiceObservabilityDbContext(options))
            {
                await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE [service].[MetricRollupSeries] DROP CONSTRAINT [CK_TestRollupFailure]", token);
                await legacy.MetricRollupWindows.ExecuteDeleteAsync(token);
                Assert.AreEqual(3, await legacy.MetricPoints.CountAsync(token));
            }
            // Pre-revision raw data can be materialized when an explicit historical window is discovered.
            Assert.AreEqual(MetricRollupStatus.Complete, await materializer.MaterializeAsync(service.Id, start, token));
            await using (var backfill = new ServiceObservabilityDbContext(options))
            {
                var saved = await backfill.MetricRollupSeries.SingleAsync(token);
                Assert.AreEqual(12L, ResourceMetrics.Parser.ParseFrom(saved.ResultPayload).ScopeMetrics.Single().Metrics.Single().Gauge.DataPoints.Single().AsInt);
                Assert.AreEqual(1L, (await backfill.MetricRollupWindows.SingleAsync(token)).InputRevision);
                var window = await backfill.MetricRollupWindows.SingleAsync(token);
                window.ProjectionVersion = 1;
                await backfill.SaveChangesAsync(token);
            }
            // An older algorithm result with raw data is rebuilt with the current algorithm.
            Assert.AreEqual(MetricRollupStatus.Complete, await materializer.MaterializeAsync(service.Id, start, token));
            await using (var future = new ServiceObservabilityDbContext(options))
            {
                var window = await future.MetricRollupWindows.SingleAsync(token);
                Assert.AreEqual(MetricRollupMaterializer.ProjectionVersion, window.ProjectionVersion);
                window.ProjectionVersion = 3;
                await future.SaveChangesAsync(token);
            }
            Assert.AreEqual(MetricRollupStatus.VersionUnsupported, await materializer.MaterializeAsync(service.Id, start, token));
            await using (var pruned = new ServiceObservabilityDbContext(options))
            {
                var window = await pruned.MetricRollupWindows.SingleAsync(token);
                window.ProjectionVersion = 1;
                window.RawPruned = true;
                await pruned.SaveChangesAsync(token);
            }
            Assert.AreEqual(MetricRollupStatus.RawPruned, await materializer.MaterializeAsync(service.Id, start, token));
            await using (var retained = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(1, (await retained.MetricRollupWindows.SingleAsync(token)).ProjectionVersion);
                var saved = await retained.MetricRollupSeries.SingleAsync(token);
                Assert.AreEqual(12L, ResourceMetrics.Parser.ParseFrom(saved.ResultPayload).ScopeMetrics.Single().Metrics.Single().Gauge.DataPoints.Single().AsInt);
            }
            Assert.AreEqual(MetricRollupStatus.WindowOpen, await materializer.MaterializeAsync(service.Id, start.AddDays(1), token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task UnknownPointIsPersistedAsIncompleteAndCannotAppearClean() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-10);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), start.AddMinutes(-5));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token); setup.Sources.Add(source); setup.Services.Add(service); await setup.SaveChangesAsync(token);
            }
            var point = new NumberDataPoint { TimeUnixNano = (ulong)start.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000, AsInt = 3 };
            var extended = NumberDataPoint.Parser.ParseFrom(point.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node-a" } });
            var metric = new Metric { Name = "load", Gauge = new Gauge { DataPoints = { extended } } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            Assert.AreEqual(MetricIngestionStatus.Accepted, await new MetricIngestion(factory).WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            var materializer = new MetricRollupMaterializer(factory);
            Assert.AreEqual(MetricRollupStatus.Incomplete, await materializer.MaterializeAsync(service.Id, start, token));
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.IsFalse((await verify.MetricRollupWindows.SingleAsync(token)).IsComplete);
            var series = await verify.MetricRollupSeries.SingleAsync(token);
            Assert.IsTrue(series.RequiresRaw);
            Assert.IsNotNull(series.InstanceId);
            Assert.IsTrue(await verify.Instances.AnyAsync(x => x.Id == series.InstanceId && x.ServiceId == service.Id, token));
            Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
        }, context.CancellationToken);
}
