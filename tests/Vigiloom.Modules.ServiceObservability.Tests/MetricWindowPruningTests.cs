using Google.Protobuf;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricWindowPruningTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ScheduledCleanupExpiresOldWindowsAndKeepsAvailableRollups() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300);
            var oldest = start.AddDays(-370);
            var middle = start.AddDays(-40);
            var recent = start.AddDays(-1);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), oldest.AddMinutes(-5));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source); setup.Services.Add(service); await setup.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            Metric Gauge(DateTimeOffset time) => new() { Name = "gauge", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = (ulong)time.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000, AsInt = 1 } } } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            var materializer = new MetricRollupMaterializer(factory);
            foreach (var time in new[] { oldest, middle, recent })
            {
                var metric = Gauge(time);
                if (time == oldest)
                    for (var i = 2; i <= 201; i++) metric.Gauge.DataPoints.Add(new NumberDataPoint {
                        TimeUnixNano = (ulong)time.AddSeconds(i).ToUnixTimeMilliseconds() * 1_000_000, AsInt = i });
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
                Assert.AreEqual(MetricRollupStatus.Complete, await materializer.MaterializeAsync(service.Id, time, token));
            }
            var cleanup = new TelemetryRetentionCleanup(factory);
            await TelemetryRetentionSettings.EnsureAsync(factory, token);
            long oldestRow;
            byte[] previousVersion;
            await using (var before = new ServiceObservabilityDbContext(options))
            {
                var window = await before.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == oldest, token);
                oldestRow = window.RowId; previousVersion = window.Version;
            }
            Assert.AreEqual(3, await new MetricRetentionCleanup(factory).ProcessAsync(service.Id, oldestRow, token));
            await using (var interrupted = new ServiceObservabilityDbContext(options))
            {
                var window = await interrupted.MetricRollupWindows.SingleAsync(x => x.RowId == oldestRow, token);
                Assert.IsFalse(previousVersion.AsSpan().SequenceEqual(window.Version));
                Assert.AreEqual(101, await interrupted.MetricPoints.CountAsync(x => x.ServiceId == service.Id && x.OccurredAt < oldest.AddMinutes(5), token));
                var policy = await interrupted.TelemetryRetentionSettings.SingleAsync(token);
                policy.MetricRawDays = 500; policy.MetricRollupDays = 500;
                await interrupted.SaveChangesAsync(token);
            }
            Assert.AreEqual(MetricRollupStatus.RawPruned, await materializer.MaterializeAsync(service.Id, oldest, token));
            Assert.AreEqual(MetricIngestionStatus.WindowPruned, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(oldest), token));
            Assert.AreEqual((0L, 1L), await cleanup.RunMetricsAsync(token));
            await using (var reset = new ServiceObservabilityDbContext(options))
            {
                var policy = await reset.TelemetryRetentionSettings.SingleAsync(token);
                policy.MetricRawDays = 30; policy.MetricRollupDays = 365;
                await reset.SaveChangesAsync(token);
            }
            Assert.AreEqual((1L, 0L), await cleanup.RunMetricsAsync(token));
            Assert.AreEqual((0L, 0L), await cleanup.RunMetricsAsync(token));
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                var windows = await verify.MetricRollupWindows.Where(x => x.ServiceId == service.Id).OrderBy(x => x.WindowStart).ToArrayAsync(token);
                Assert.HasCount(2, windows);
                Assert.AreEqual(middle, windows[0].WindowStart); Assert.IsTrue(windows[0].RawPruned);
                Assert.AreEqual(recent, windows[1].WindowStart); Assert.IsFalse(windows[1].RawPruned);
                Assert.AreEqual(1, await verify.MetricPoints.CountAsync(x => x.ServiceId == service.Id, token));
                Assert.AreEqual(1, await verify.MetricContexts.CountAsync(x => x.ServiceId == service.Id, token));
                Assert.AreEqual(oldest.AddMinutes(5), (await verify.Services.SingleAsync(x => x.Id == service.Id, token)).MetricRetainedAfter);
            }
            // An extension cannot recreate the deleted window as a partial history.
            Assert.AreEqual(MetricIngestionStatus.WindowPruned, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(oldest), token));
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), Gauge(recent), token));
            await using var downgrade = new ServiceObservabilityDbContext(options);
            var previous = downgrade.Database.GetMigrations().Single(x => x.EndsWith("_TelemetryRetention", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => downgrade.GetService<IMigrator>().MigrateAsync(previous, token));
            Assert.AreEqual(51000, error.Number);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PruningKeepsAdjacentWindowAndRejectsLatePoints() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddDays(-40);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), start.AddMinutes(-5));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source); setup.Services.Add(service); await setup.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var boundary = (ulong)start.AddMinutes(5).ToUnixTimeMilliseconds() * 1_000_000;
            var gauge = new Metric { Name = "gauge", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = boundary - 1_000_000_000, AsInt = 1 },
                new NumberDataPoint { TimeUnixNano = boundary, AsInt = 2 } } } };
            var delta = new Metric { Name = "delta", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta,
                DataPoints = { new NumberDataPoint { StartTimeUnixNano = boundary - 300_000_000_000, TimeUnixNano = boundary, AsInt = 3 },
                    new NumberDataPoint { StartTimeUnixNano = boundary, TimeUnixNano = boundary + 1, AsInt = 4 } } } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), gauge, token));
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), delta, token));
            Assert.AreEqual(MetricRollupStatus.Complete, await new MetricRollupMaterializer(factory).MaterializeAsync(service.Id, start, token));

            await using (var cleanup = new ServiceObservabilityDbContext(options))
            {
                await using var transaction = await cleanup.Database.BeginTransactionAsync(token);
                Assert.IsTrue(await ServiceInstanceResolver.LockServiceAsync(cleanup.Database.GetDbConnection(), transaction.GetDbTransaction(), service.Id, token));
                var state = await cleanup.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == start, token);
                Assert.IsTrue(await MetricWindowPruning.PruneAsync(cleanup, state, token));
                await transaction.CommitAsync(token);
            }
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                var points = await verify.MetricPoints.Where(x => x.ServiceId == service.Id).ToArrayAsync(token);
                CollectionAssert.AreEquivalent(new ulong[] { boundary, boundary + 1 },
                    points.Select(x => NumberDataPoint.Parser.ParseFrom(x.PointPayload).TimeUnixNano).ToArray());
                Assert.IsTrue((await verify.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == start, token)).RawPruned);
                Assert.IsTrue(await verify.MetricRollupSeries.AnyAsync(x => x.ServiceId == service.Id, token));
            }
            gauge.Gauge.DataPoints.RemoveAt(1);
            Assert.AreEqual(MetricIngestionStatus.WindowPruned, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), gauge, token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task IncompleteOrStaleRollupCannotDeleteRawData() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
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
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var metric = new Metric { Name = "gauge", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = (ulong)start.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000, AsInt = 1 } } } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            await using (var cleanup = new ServiceObservabilityDbContext(options))
            {
                await using var transaction = await cleanup.Database.BeginTransactionAsync(token);
                Assert.IsTrue(await ServiceInstanceResolver.LockServiceAsync(cleanup.Database.GetDbConnection(), transaction.GetDbTransaction(), service.Id, token));
                var state = await cleanup.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == start, token);
                Assert.IsFalse(await MetricWindowPruning.PruneAsync(cleanup, state, token));
            }
            Assert.AreEqual(MetricRollupStatus.Complete, await new MetricRollupMaterializer(factory).MaterializeAsync(service.Id, start, token));
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            await using (var cleanup = new ServiceObservabilityDbContext(options))
            {
                await using var transaction = await cleanup.Database.BeginTransactionAsync(token);
                Assert.IsTrue(await ServiceInstanceResolver.LockServiceAsync(cleanup.Database.GetDbConnection(), transaction.GetDbTransaction(), service.Id, token));
                var state = await cleanup.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == start, token);
                Assert.IsFalse(await MetricWindowPruning.PruneAsync(cleanup, state, token));
            }
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(2, await verify.MetricPoints.CountAsync(x => x.ServiceId == service.Id, token));
        }, context.CancellationToken);
}
