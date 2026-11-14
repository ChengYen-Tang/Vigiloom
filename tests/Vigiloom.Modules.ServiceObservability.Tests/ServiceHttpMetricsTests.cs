using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ServiceHttpMetricsTests(TestContext context)
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ReadsRealIngestedMetricsWithoutScheduledProjection(int scenario) =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-10);
            var source = new ServiceSource("http-source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), start.AddDays(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "orders" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node-a" } });
            var scope = new ScopeMetrics { Scope = new InstrumentationScope { Name = "Microsoft.AspNetCore.Hosting", Version = "10" } };
            var first = (ulong)start.AddMinutes(1).ToUnixTimeMilliseconds() * 1_000_000;
            var last = (ulong)start.AddMinutes(2).ToUnixTimeMilliseconds() * 1_000_000;
            var epoch = (ulong)start.AddMinutes(-1).ToUnixTimeMilliseconds() * 1_000_000;
            var ingest = new MetricIngestion(factory);
            Metric? metric = null;
            foreach (var (code, count, seconds) in new[] { (200, 8UL, 1.6), (500, 2UL, 0.8) })
            {
                var histogram = new Histogram { AggregationTemporality = AggregationTemporality.Cumulative };
                var attribute = new KeyValue { Key = "http.response.status_code", Value = new AnyValue { IntValue = code } };
                var firstPoint = new HistogramDataPoint { StartTimeUnixNano = epoch, TimeUnixNano = first, Count = 3, Sum = 0.3, BucketCounts = { 3 }, Attributes = { attribute } };
                var lastPoint = new HistogramDataPoint { StartTimeUnixNano = epoch, TimeUnixNano = last, Count = 3 + count, Sum = 0.3 + seconds, BucketCounts = { 3 + count }, Attributes = { attribute } };
                if (code == 500)
                {
                    firstPoint.Attributes.Add(new KeyValue { Key = "error.type", Value = new AnyValue { StringValue = "500" } });
                    lastPoint.Attributes.Add(new KeyValue { Key = "error.type", Value = new AnyValue { StringValue = "500" } });
                    firstPoint.Attributes.Add(new KeyValue { Key = "aspnetcore.request.is_unhandled", Value = new AnyValue { BoolValue = true } });
                    lastPoint.Attributes.Add(new KeyValue { Key = "aspnetcore.request.is_unhandled", Value = new AnyValue { BoolValue = true } });
                }
                else
                {
                    firstPoint.Attributes.Add(new KeyValue { Key = "http.route", Value = new AnyValue { StringValue = "/orders" } });
                    lastPoint.Attributes.Add(new KeyValue { Key = "http.route", Value = new AnyValue { StringValue = "/orders" } });
                }
                histogram.DataPoints.Add(firstPoint); histogram.DataPoints.Add(lastPoint);
                metric = new Metric { Name = "http.server.request.duration", Unit = "s", Histogram = histogram };
                var exportResource = resource.Clone();
                if (code == 500)
                {
                    var reordered = exportResource.Resource.Attributes.Reverse().ToArray();
                    exportResource.Resource.Attributes.Clear(); exportResource.Resource.Attributes.Add(reordered);
                }
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, exportResource, scope, metric, token));
            }
            var result = await new ServiceHttpMetricsReader(factory).ReadAsync(service.Id, token);
            if (result.State != "Available")
            {
                await using var diagnostic = new ServiceObservabilityDbContext(options);
                MetricWindow.TryForSample((ulong)start.ToUnixTimeMilliseconds() * 1_000_000, out var metricWindow);
                var raw = await MetricWindowInputs.ReadAsync(diagnostic, service.Id, metricWindow, token);
                var grouped = MetricWindowGrouping.Group(source.Id, service.Id, metricWindow, raw.Points);
                var details = grouped.Series.Select(x => MetricRollupEncoding.Encode(x, metricWindow))
                    .Select(x => $"{x?.Mode}:{x?.QualityFlags}:{x?.ResultPayload?.Length}");
                Assert.Fail($"State={result.State}, raw={raw.Points.Count}, requiresRaw={grouped.RequiresRaw}, series={string.Join(',', details)}");
            }
            Assert.AreEqual("Available", result.State);
            Assert.AreEqual(10L, result.RequestCount);
            Assert.AreEqual(240d, result.MeanDurationMilliseconds!.Value, 0.001);
            Assert.AreEqual(0.2d, result.ErrorRatio!.Value, 0.001);
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(0, await verify.MetricRollupSeries.CountAsync(token));
            if (scenario == 1)
            {
                Assert.AreEqual(MetricRollupStatus.Complete, await new MetricRollupMaterializer(factory).MaterializeAsync(service.Id, start, token));
                await using (var transaction = await verify.Database.BeginTransactionAsync(token))
                {
                    await verify.MetricPoints.Where(x => x.ServiceId == service.Id).ExecuteDeleteAsync(token);
                    var state = await verify.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id, token);
                    state.RawPruned = true;
                    await verify.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                }
                var projected = await new ServiceHttpMetricsReader(factory).ReadAsync(service.Id, token);
                Assert.AreEqual("Available", projected.State);
                Assert.AreEqual(10L, projected.RequestCount);
                Assert.AreEqual(240d, projected.MeanDurationMilliseconds!.Value, 0.001);
            }
            else if (scenario == 0)
            {
                var duplicateProfile = resource.Clone();
                duplicateProfile.Resource.Attributes.Add(new KeyValue { Key = "example.pipeline", Value = new AnyValue { StringValue = "other" } });
                Assert.IsNotNull(metric);
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, duplicateProfile, scope, metric, token));
                var ambiguous = await new ServiceHttpMetricsReader(factory).ReadAsync(service.Id, token);
                Assert.AreEqual("AmbiguousSource", ambiguous.State);
                Assert.IsNull(ambiguous.RequestCount);
            }
            else
            {
                Assert.IsNotNull(metric);
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, scope, metric, token));
                var replay = await new ServiceHttpMetricsReader(factory).ReadAsync(service.Id, token);
                Assert.AreEqual("Incomplete", replay.State);
                Assert.IsNull(replay.RequestCount);
            }
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task SummarizesOnlyCompatibleReceivedHistograms(bool cumulative) =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-10);
            var source = new ServiceSource("http-source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), start.AddDays(-1));
            var instance = Guid.CreateVersion7();
            var window = new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1,
                ProjectedRevision = 1, ProjectionVersion = MetricRollupMaterializer.ProjectionVersion, ProjectedAt = now, IsComplete = true, RawPruned = true };
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.Add(source); db.Services.Add(service); db.MetricRollupWindows.Add(window);
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instance},{service.Id},{source.Id},{"node-a"},{now},{now})", token);
                foreach (var (status, population, seconds) in new[] { (200, 8UL, 1.6), (500, 2UL, 0.8) })
                {
                    var from = (ulong)start.ToUnixTimeMilliseconds() * 1_000_000;
                    var to = (ulong)start.AddMinutes(5).ToUnixTimeMilliseconds() * 1_000_000;
                    var attributes = new KeyValue { Key = "http.response.status_code", Value = new AnyValue { IntValue = status } };
                    var histogram = new Histogram { AggregationTemporality = cumulative ? AggregationTemporality.Cumulative : AggregationTemporality.Delta };
                    if (cumulative) histogram.DataPoints.Add(new HistogramDataPoint { StartTimeUnixNano = from - 1, TimeUnixNano = from, Count = 3, Sum = 0.3, Attributes = { attributes } });
                    histogram.DataPoints.Add(new HistogramDataPoint { StartTimeUnixNano = cumulative ? from - 1 : from, TimeUnixNano = to,
                        Count = population + (cumulative ? 3UL : 0UL), Sum = seconds + (cumulative ? 0.3 : 0), Attributes = { attributes } });
                    var payload = new ResourceMetrics { Resource = new Resource(), ScopeMetrics = { new ScopeMetrics { Scope = new InstrumentationScope { Name = "Microsoft.AspNetCore.Hosting", Version = "10" },
                        Metrics = { new Metric { Name = "http.server.request.duration", Unit = "s", Histogram = histogram } } } } }.ToByteArray();
                    var hash = new byte[32]; BitConverter.GetBytes(status).CopyTo(hash, 0);
                    db.MetricRollupSeries.Add(new MetricRollupSeries { WindowRowId = window.RowId, ServiceId = service.Id, InstanceId = instance,
                        IdentityHash = hash, IdentityBytes = [1], Mode = cumulative ? MetricRollupMode.CumulativeHistogramSnapshots : MetricRollupMode.DeltaHistogram,
                        InputCount = cumulative ? 2 : 1, ResultVersion = 1, ResultPayload = payload });
                }
                await db.SaveChangesAsync(token);
            }
            var reader = new ServiceHttpMetricsReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var result = await reader.ReadAsync(service.Id, token);
            Assert.AreEqual("Available", result.State);
            Assert.AreEqual(10L, result.RequestCount);
            Assert.AreEqual(240d, result.MeanDurationMilliseconds!.Value, 0.001);
            Assert.AreEqual(0.2d, result.ErrorRatio!.Value, 0.001);
            Assert.AreEqual(start, result.ObservedFrom);
            Assert.AreEqual(start.AddMinutes(5), result.ObservedTo);

            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var series = await db.MetricRollupSeries.FirstAsync(token);
                series.QualityFlags = (long)MetricRollupQuality.Overlap;
                await db.SaveChangesAsync(token);
            }
            var invalid = await reader.ReadAsync(service.Id, token);
            Assert.AreEqual("Incomplete", invalid.State);
            Assert.IsNull(invalid.MeanDurationMilliseconds);
            Assert.IsNull(invalid.ErrorRatio);
        }, context.CancellationToken);
}
