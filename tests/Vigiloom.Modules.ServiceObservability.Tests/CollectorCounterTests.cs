using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class CollectorCounterTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Counters_RespectMappingsInstancesDimensionsAndLateSamples() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var monitor = new ServiceSource("monitor", new byte[32], now);
            var business = new ServiceSource("business", new byte[32], now);
            var another = new ServiceSource("another", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(monitor.Id, "collector", "prod", "Collector", now), now.AddHours(-1));
            var collector = new CollectorRegistration("collector", service.Id, now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(monitor, business, another); db.Services.Add(service); db.Collectors.Add(collector);
                db.CollectorRoutes.Add(new CollectorRoute(collector.Id, business.Id, "otlp_http/a"));
                await db.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            async Task Write(string instance, string exporter, string dimension, long value, DateTimeOffset measured,
                AggregationTemporality temporality = AggregationTemporality.Cumulative, string? host = null, uint flags = 0,
                string metricName = "otelcol_exporter_sent_log_records")
            {
                var resource = new ResourceMetrics { Resource = new Resource() };
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "collector" } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instance } });
                if (host is not null) resource.Resource.Attributes.Add(new KeyValue { Key = "host.name", Value = new AnyValue { StringValue = host } });
                var point = new NumberDataPoint { TimeUnixNano = (ulong)measured.ToUnixTimeMilliseconds() * 1_000_000,
                    StartTimeUnixNano = (ulong)now.AddSeconds(-55).ToUnixTimeMilliseconds() * 1_000_000, AsInt = value, Flags = flags };
                point.Attributes.Add(new KeyValue { Key = "exporter", Value = new AnyValue { StringValue = exporter } });
                point.Attributes.Add(new KeyValue { Key = "server.address", Value = new AnyValue { StringValue = dimension } });
                var metric = new Metric { Name = metricName, Sum = new Sum { IsMonotonic = true, AggregationTemporality = temporality, DataPoints = { point } } };
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitor.Id, resource, new ScopeMetrics(), metric, token));
            }
            await Write("a", "otlp_http/a", "first", 10, now);
            await Write("a", "otlp_http/a", "first", 2, now.AddSeconds(-10)); // arrives later
            await Write("a", "otlp_http/a", "second", 20, now);
            await Write("a", "otlp_http/b", "other", 30, now);
            await Write("b", "otlp_http/a", "first", 40, now);
            await Write("a", "otlp_http/a", "first", 3, now.AddSeconds(-50), metricName: "otelcol_exporter_send_failed_log_records");
            await Write("a", "otlp_http/a", "first", 4, now.AddSeconds(-30), metricName: "otelcol_exporter_send_failed_log_records");
            await Write("a", "otlp_http/a", "first", 6, now.AddSeconds(-10), metricName: "otelcol_exporter_send_failed_log_records");
            Guid instanceA, instanceB;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                instanceA = await db.Instances.Where(x => x.ServiceId == service.Id && x.ExternalInstanceId == "a").Select(x => x.Id).SingleAsync(token);
                instanceB = await db.Instances.Where(x => x.ServiceId == service.Id && x.ExternalInstanceId == "b").Select(x => x.Id).SingleAsync(token);
            }
            var query = new CollectorCounters(factory);
            var a = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(a);
            Assert.IsFalse(a.Incomplete);
            Assert.HasCount(3, a.Counters);
            CollectionAssert.AreEquivalent(new[] { "10", "20", "6" }, a.Counters.Select(x => x.Value).ToArray());
            Assert.AreEqual(FailureObservation.Increased, a.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
            Assert.AreEqual(FailureObservation.Unknown, a.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_spans").Observation);
            long futureId;
            DateTimeOffset originalReceipt;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var failureName = System.Text.Encoding.Unicode.GetBytes("otelcol_exporter_send_failed_log_records");
                var clockSql = db.MetricPoints.Where(x => x.ReceivedAt > DateTimeOffset.UtcNow).ToQueryString();
                Assert.Contains("SYSUTCDATETIME", clockSql, StringComparison.OrdinalIgnoreCase);
                var latestFailure = await db.MetricPoints.Where(x => x.ServiceId == service.Id && x.Context.MetricNameBytes == failureName)
                    .OrderByDescending(x => x.OccurredAt).FirstAsync(token);
                futureId = latestFailure.RowId; originalReceipt = latestFailure.ReceivedAt;
                latestFailure.ReceivedAt = DateTimeOffset.UtcNow.AddHours(1);
                await db.SaveChangesAsync(token);
            }
            var future = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(future);
            Assert.AreEqual(FailureObservation.Unknown, future.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
            await using (var db = new ServiceObservabilityDbContext(options))
                await db.MetricPoints.Where(x => x.RowId == futureId).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceivedAt, originalReceipt), token);
            await using (var db = new ServiceObservabilityDbContext(options))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Sources] SET [RevokedAt] = {now} WHERE [Id] = {business.Id}", token);
            var revoked = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(revoked);
            Assert.IsNotNull(revoked.TargetSourceRevokedAt);
            Assert.AreEqual(FailureObservation.Unknown, revoked.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
            await using (var db = new ServiceObservabilityDbContext(options))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Sources] SET [RevokedAt] = NULL WHERE [Id] = {business.Id}", token);
            Assert.AreNotEqual(a.Counters[0].Series, a.Counters[1].Series);
            Assert.IsFalse(a.Counters.Any(x => x.Series.Contains("first", StringComparison.Ordinal)));
            await Write("a", "otlp_http/a", "first", 50, now, host: "other-host");
            var differentResource = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(differentResource);
            Assert.HasCount(4, differentResource.Counters);
            await Write("a", "otlp_http/a", "first", 0, now.AddMilliseconds(1), flags: (uint)DataPointFlags.NoRecordedValueMask);
            var missingValue = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(missingValue);
            Assert.IsTrue(missingValue.Incomplete);
            CollectionAssert.AreEquivalent(new[] { "20", "50", "6" }, missingValue.Counters.Select(x => x.Value).ToArray());
            Assert.AreEqual(FailureObservation.Increased, missingValue.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
            var b = await query.QueryAsync(collector.Id, business.Id, instanceB, token);
            Assert.IsNotNull(b);
            Assert.AreEqual("40", b.Counters.Single().Value);
            Assert.IsNull(await query.QueryAsync(collector.Id, another.Id, instanceA, token));
            await Write("a", "otlp_http/a", "invalid", 1, now, AggregationTemporality.Delta);
            var unsupported = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(unsupported);
            Assert.IsTrue(unsupported.Incomplete);
            Assert.HasCount(3, unsupported.Counters);
            await Write("a", "otlp_http/b", "second", 77, now.AddMilliseconds(2), AggregationTemporality.Delta);
            var otherExporter = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(otherExporter);
            CollectionAssert.AreEquivalent(new[] { "20", "50", "6" }, otherExporter.Counters.Select(x => x.Value).ToArray());
            await Write("a", "otlp_http/a", "first", 3, now.AddMilliseconds(3), AggregationTemporality.Delta, host: "other-host");
            var latestUnsupported = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(latestUnsupported);
            Assert.IsTrue(latestUnsupported.Incomplete);
            CollectionAssert.AreEquivalent(new[] { "20", "6" }, latestUnsupported.Counters.Select(x => x.Value).ToArray());
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "collector" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "a" } });
            var historicalFailures = new Metric { Name = "otelcol_exporter_send_failed_log_records", Sum = new Sum { IsMonotonic = true, AggregationTemporality = AggregationTemporality.Cumulative } };
            for (var i = 0; i < 130; i++)
            {
                var old = new NumberDataPoint { StartTimeUnixNano = (ulong)now.AddMinutes(-30).ToUnixTimeMilliseconds() * 1_000_000,
                    TimeUnixNano = (ulong)now.AddMinutes(-10).ToUnixTimeMilliseconds() * 1_000_000, AsInt = i };
                old.Attributes.Add(new KeyValue { Key = "exporter", Value = new AnyValue { StringValue = "otlp_http/a" } });
                old.Attributes.Add(new KeyValue { Key = "server.address", Value = new AnyValue { StringValue = "first" } });
                historicalFailures.Sum.DataPoints.Add(old);
            }
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitor.Id, resource, new ScopeMetrics(), historicalFailures, token));
            var historical = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(historical);
            Assert.IsTrue(historical.Incomplete);
            Assert.AreEqual(FailureObservation.Increased, historical.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
            var large = new Metric { Name = "otelcol_exporter_sent_log_records", Sum = new Sum { IsMonotonic = true, AggregationTemporality = AggregationTemporality.Cumulative } };
            for (var i = 0; i < 6; i++)
            {
                var point = new NumberDataPoint { TimeUnixNano = (ulong)now.AddSeconds(2).ToUnixTimeMilliseconds() * 1_000_000, AsInt = i };
                point.Attributes.Add(new KeyValue { Key = "exporter", Value = new AnyValue { StringValue = "otlp_http/a" } });
                point.Attributes.Add(new KeyValue { Key = "large.dimension", Value = new AnyValue { StringValue = new string((char)('a' + i), 200_000) } });
                large.Sum.DataPoints.Add(point);
            }
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitor.Id, resource, new ScopeMetrics(), large, token));
            var bytesLimited = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(bytesLimited);
            Assert.IsTrue(bytesLimited.Incomplete);
            Assert.IsLessThan(6, bytesLimited.Counters.Count(x => x.Value is "0" or "1" or "2" or "3" or "4" or "5"));
            var burst = new Metric { Name = "otelcol_exporter_sent_log_records", Sum = new Sum { IsMonotonic = true, AggregationTemporality = AggregationTemporality.Cumulative } };
            for (var i = 0; i < 130; i++)
            {
                var point = new NumberDataPoint { TimeUnixNano = (ulong)now.AddSeconds(3).ToUnixTimeMilliseconds() * 1_000_000, AsInt = i };
                point.Attributes.Add(new KeyValue { Key = "exporter", Value = new AnyValue { StringValue = "otlp_http/a" } });
                point.Attributes.Add(new KeyValue { Key = "test.dimension", Value = new AnyValue { IntValue = i } });
                burst.Sum.DataPoints.Add(point);
            }
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitor.Id, resource, new ScopeMetrics(), burst, token));
            var truncated = await query.QueryAsync(collector.Id, business.Id, instanceA, token);
            Assert.IsNotNull(truncated);
            Assert.IsTrue(truncated.Incomplete);
            Assert.HasCount(128, truncated.Counters);
            Assert.AreEqual(FailureObservation.Increased, truncated.Failures.Single(x => x.Metric == "otelcol_exporter_send_failed_log_records").Observation);
        }, context.CancellationToken);
}
