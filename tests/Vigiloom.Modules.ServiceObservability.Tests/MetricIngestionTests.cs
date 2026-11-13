using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricIngestionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task CollectorObservations_SeparatesInstancesAndExcludesOtherNamesAndServices() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("monitor", new byte[32], now);
            var secondSource = new ServiceSource("other monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "collector", "prod", "Collector", now), now.AddMinutes(-1));
            var other = new MonitoredService(new ServiceCandidate(source.Id, "other", "prod", "Other", now), now.AddMinutes(-1));
            var otherScope = new MonitoredService(new ServiceCandidate(secondSource.Id, "collector", "prod", "Collector", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.AddRange(source, secondSource); db.Services.AddRange(service, other, otherScope); await db.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var ingest = new MetricIngestion(factory);
            async Task Write(string serviceName, string? instance, string name, Guid? sourceId = null, bool gauge = false, DateTimeOffset? measuredAt = null)
            {
                var resource = new ResourceMetrics { Resource = new Resource() };
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = serviceName } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
                if (instance is not null) resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instance } });
                var point = new NumberDataPoint { TimeUnixNano = (ulong)(measuredAt ?? now).ToUnixTimeMilliseconds() * 1_000_000, AsDouble = 1 };
                var metric = new Metric { Name = name };
                if (gauge) metric.Gauge = new Gauge { DataPoints = { point } };
                else metric.Sum = new Sum { DataPoints = { point } };
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(sourceId ?? source.Id, resource, new ScopeMetrics(), metric, token));
            }
            await Write("collector", "node-a", "otelcol_process_uptime");
            // A delayed older sample must not replace the latest measurement merely because it arrived later.
            await Write("collector", "node-a", "otelcol_process_uptime", measuredAt: now.AddSeconds(-30));
            await Write("collector", "node-b", "another_metric");
            await Write("collector", "node-b", "otelcol_process_uptime", gauge: true);
            await Write("collector", null, "otelcol_process_uptime");
            await Write("other", "node-b", "otelcol_process_uptime");
            await Write("collector", "node-b", "otelcol_process_uptime", secondSource.Id);
            var observations = await new CollectorObservations(factory).QueryAsync(service.Id, 1, token);
            Assert.IsNotNull(observations);
            Assert.AreEqual(source.Id, observations.SourceId);
            Assert.HasCount(2, observations.Instances);
            Assert.IsNotNull(observations.Instances.Single(x => x.ExternalInstanceId == "node-a").LastReceivedAt);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
                observations.Instances.Single(x => x.ExternalInstanceId == "node-a").LastMeasuredAt);
            Assert.IsNull(observations.Instances.Single(x => x.ExternalInstanceId == "node-b").LastReceivedAt);
            Assert.IsNotNull(observations.UnidentifiedReceivedAt);
            Assert.IsFalse(observations.HasMore);
            Assert.IsNull(await new CollectorObservations(factory).QueryAsync(Guid.CreateVersion7(), 1, token));
            var reapproved = now.AddMinutes(1);
            var skewedSample = now.AddMinutes(2);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[MetricPoints] SET [OccurredAt] = {skewedSample} WHERE [ServiceId] = {service.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Services] SET [ApprovedAt] = {reapproved} WHERE [Id] = {service.Id}", token);
            }
            var afterReapproval = await new CollectorObservations(factory).QueryAsync(service.Id, 1, token);
            Assert.IsNotNull(afterReapproval);
            Assert.IsTrue(afterReapproval.Instances.All(x => x.LastReceivedAt is null));
            Assert.IsNull(afterReapproval.UnidentifiedReceivedAt);
            var originalApproval = now.AddMinutes(-1);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Services] SET [ApprovedAt] = {originalApproval} WHERE [Id] = {service.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[MetricPoints] SET [OccurredAt] = {now} WHERE [ServiceId] = {service.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Sources] SET [RevokedAt] = {now} WHERE [Id] = {source.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Services] SET [RevokedAt] = {now} WHERE [Id] = {service.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Instances] SET [RetiredAt] = {now} WHERE [ServiceId] = {service.Id}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[MetricPoints] SET [ReceivedAt] = DATEADD(day, -2, [ReceivedAt]) WHERE [ServiceId] = {service.Id}", token);
            }
            var expired = await new CollectorObservations(factory).QueryAsync(service.Id, 1, token);
            Assert.IsNotNull(expired);
            Assert.IsNotNull(expired.SourceRevokedAt);
            Assert.IsNotNull(expired.ServiceStoppedAt);
            Assert.IsTrue(expired.Instances.All(x => x.RetiredAt is not null && x.LastReceivedAt is null));
            Assert.IsNull(expired.UnidentifiedReceivedAt);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                for (var i = 0; i < 51; i++)
                {
                    var id = Guid.CreateVersion7();
                    var external = $"extra-{i}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({id},{service.Id},{source.Id},{external},{now},{now})", token);
                }
            }
            var firstPage = await new CollectorObservations(factory).QueryAsync(service.Id, 1, token);
            var secondPage = await new CollectorObservations(factory).QueryAsync(service.Id, 2, token);
            Assert.IsNotNull(firstPage); Assert.IsNotNull(secondPage);
            Assert.HasCount(50, firstPage.Instances);
            Assert.IsTrue(firstPage.HasMore);
            Assert.HasCount(3, secondPage.Instances);
            Assert.IsFalse(secondPage.HasMore);
            Assert.IsEmpty(firstPage.Instances.Select(x => x.InstanceId).Intersect(secondPage.Instances.Select(x => x.InstanceId)));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MetricNameLookup_PreservesLongNamesAndDistinguishesExactNames() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "collector", "prod", "Collector", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "collector" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var ingest = new MetricIngestion(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var timestamp = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            foreach (var name in new[] { "cpu", "cpu ", "CPU", new string('x', 257) })
            {
                var metric = new Metric { Name = name, Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = timestamp, AsDouble = 1 } } } };
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            }
            await using var check = new ServiceObservabilityDbContext(options);
            var exact = System.Text.Encoding.Unicode.GetBytes("cpu");
            var names = await check.MetricContexts.Where(x => x.ServiceId == service.Id && x.MetricNameBytes == exact).ToArrayAsync(token);
            Assert.HasCount(1, names);
            Assert.AreEqual("cpu", Metric.Parser.ParseFrom(names[0].MetricPayload).Name);
            var longName = await check.MetricContexts.SingleAsync(x => x.ServiceId == service.Id && x.MetricName == null, token);
            Assert.AreEqual(new string('x', 257), Metric.Parser.ParseFrom(longName.MetricPayload).Name);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MetricExport_SeparatesCandidatesAndRollsBackAllOnFailure() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "api", "prod", "API", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token); db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[FailMetricBatch] ON [service].[MetricPoints] INSTEAD OF INSERT AS THROW 51009, 'test failure', 1", token);
            }
            var nanos = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            ResourceMetrics ResourceFor(string name)
            {
                var resource = new ResourceMetrics { Resource = new Resource() };
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
                var scope = new ScopeMetrics();
                scope.Metrics.Add(new Metric { Name = "cpu", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = nanos, AsDouble = 1 } } } });
                scope.Metrics.Add(new Metric { Name = "ram", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = nanos, AsDouble = 2 } } } });
                resource.ScopeMetrics.Add(scope);
                return resource;
            }
            var request = new ExportMetricsServiceRequest { ResourceMetrics = { ResourceFor("api"), ResourceFor("pending") } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new MetricExport(factory, new CandidateDiscovery(factory));
            var oversized = ResourceFor("api");
            oversized.ScopeMetrics.Clear();
            var largeScope = new ScopeMetrics();
            foreach (var name in new[] { "one", "two" })
            {
                var metric = new Metric { Name = name, Gauge = new Gauge() };
                for (var i = 0; i < 5_001; i++) metric.Gauge.DataPoints.Add(new NumberDataPoint { TimeUnixNano = nanos });
                largeScope.Metrics.Add(metric);
            }
            oversized.ScopeMetrics.Add(largeScope);
            var tooMany = new ExportMetricsServiceRequest { ResourceMetrics = { oversized } };
            Assert.AreEqual(ExportStatus.Invalid, (await export.WriteAsync(source.Id, tooMany, token)).Status);
            await using (var before = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await before.MetricPoints.CountAsync(token));
                Assert.AreEqual(0, await before.Candidates.CountAsync(token));
            }
            await Assert.ThrowsAsync<DbUpdateException>(() => export.WriteAsync(source.Id, request, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.MetricPoints.CountAsync(token));
                Assert.AreEqual(0, await db.MetricContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[FailMetricBatch]", token);
            }
            var result = await export.WriteAsync(source.Id, request, token);
            Assert.AreEqual(ExportStatus.Partial, result.Status);
            Assert.AreEqual(2L, result.Rejected);
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(2, await check.MetricPoints.CountAsync(token));
            Assert.AreEqual(1, await check.Candidates.CountAsync(token));
            Assert.AreEqual("pending", (await check.Candidates.SingleAsync(token)).ServiceKey);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Metrics_AuthorizePersistAllTypesAndRollbackOnFailure() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var nanos = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            var source = new ServiceSource("source", new byte[32], now);
            var other = new ServiceSource("other", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "api", "prod", "API", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(source, other); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            var resource = new ResourceMetrics { Resource = new Resource(), SchemaUrl = "resource-schema" };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "api" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
            var scope = new ScopeMetrics { SchemaUrl = "scope-schema" };
            var metrics = new[]
            {
                new Metric { Name = "gauge", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = nanos, AsDouble = 1.5 } } } },
                new Metric { Name = "sum", Sum = new Sum { IsMonotonic = true, DataPoints = { new NumberDataPoint { TimeUnixNano = nanos, AsInt = 2 } } } },
                new Metric { Name = "histogram", Histogram = new Histogram { DataPoints = { new HistogramDataPoint { TimeUnixNano = nanos, Count = 3 } } } },
                new Metric { Name = "exponential", ExponentialHistogram = new ExponentialHistogram { DataPoints = { new ExponentialHistogramDataPoint { TimeUnixNano = nanos, Count = 4 } } } },
                new Metric { Name = "summary", Summary = new Summary { DataPoints = { new SummaryDataPoint { TimeUnixNano = nanos, Count = 5 } } } }
            };
            var ingestion = new MetricIngestion(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var oversized = new Metric { Name = "too-many", Gauge = new Gauge() };
            for (var i = 0; i < 10_001; i++) oversized.Gauge.DataPoints.Add(new NumberDataPoint { TimeUnixNano = nanos });
            Assert.AreEqual(MetricIngestionStatus.Invalid, await ingestion.WriteAsync(source.Id, resource, scope, oversized, token));
            metrics[1].Sum.DataPoints[0].StartTimeUnixNano = nanos + 1;
            Assert.AreEqual(MetricIngestionStatus.Invalid, await ingestion.WriteAsync(source.Id, resource, scope, metrics[1], token));
            metrics[1].Sum.DataPoints[0].StartTimeUnixNano = 0;
            Assert.AreEqual(MetricIngestionStatus.Unauthorized, await ingestion.WriteAsync(other.Id, resource, scope, metrics[0], token));
            metrics[0].Gauge.DataPoints[0].TimeUnixNano = (ulong)now.AddMinutes(-2).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(MetricIngestionStatus.BeforeApproval, await ingestion.WriteAsync(source.Id, resource, scope, metrics[0], token));
            metrics[0].Gauge.DataPoints[0].TimeUnixNano = nanos;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.MetricPoints.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[FailMetricInsert] ON [service].[MetricPoints] INSTEAD OF INSERT AS THROW 51006, 'test failure', 1", token);
            }
            await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.WriteAsync(source.Id, resource, scope, metrics[0], token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.MetricPoints.CountAsync(token));
                Assert.AreEqual(0, await db.MetricContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[FailMetricInsert]", token);
            }
            foreach (var metric in metrics)
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingestion.WriteAsync(source.Id, resource, scope, metric, token));
            await using var check = new ServiceObservabilityDbContext(options);
            var rows = await check.MetricPoints.Include(x => x.Context).OrderBy(x => x.Kind).ToListAsync(token);
            Assert.HasCount(5, rows);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, rows.Select(x => x.Kind).ToArray());
            Assert.AreEqual("gauge", Metric.Parser.ParseFrom(rows[0].Context.MetricPayload).Name);
            Assert.IsEmpty(Metric.Parser.ParseFrom(rows[0].Context.MetricPayload).Gauge.DataPoints);
            Assert.AreEqual(1.5, NumberDataPoint.Parser.ParseFrom(rows[0].PointPayload).AsDouble);
            Assert.IsTrue(Metric.Parser.ParseFrom(rows[1].Context.MetricPayload).Sum.IsMonotonic);
            Assert.AreEqual(2, NumberDataPoint.Parser.ParseFrom(rows[1].PointPayload).AsInt);
            Assert.AreEqual(3UL, HistogramDataPoint.Parser.ParseFrom(rows[2].PointPayload).Count);
            Assert.AreEqual(4UL, ExponentialHistogramDataPoint.Parser.ParseFrom(rows[3].PointPayload).Count);
            Assert.AreEqual(5UL, SummaryDataPoint.Parser.ParseFrom(rows[4].PointPayload).Count);
            Assert.AreEqual("scope-schema", rows[0].Context.ScopeSchemaUrl);
            Assert.IsNotNull(rows[0].InstanceId);
        }, context.CancellationToken);
}
