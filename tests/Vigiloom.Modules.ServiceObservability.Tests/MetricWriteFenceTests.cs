using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
public sealed class MetricWriteFenceTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task UnidentifiedMetricWaitsForServiceFenceAndCanRetryAfterRelease() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddMinutes(-1));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source);
                setup.Services.Add(service);
                await setup.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var resource = new ResourceMetrics { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var metric = new Metric { Name = "value", Gauge = new Gauge { DataPoints = { new NumberDataPoint
            {
                TimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000, AsInt = 42
            } } } };
            var ingestion = new MetricIngestion(factory);
            await using (var blocker = await factory.CreateDbContextAsync(token))
            await using (var transaction = await blocker.Database.BeginTransactionAsync(token))
            {
                Assert.IsTrue(await ServiceInstanceResolver.LockServiceAsync(blocker.Database.GetDbConnection(),
                    transaction.GetDbTransaction(), service.Id, token));
                Assert.AreEqual(MetricIngestionStatus.Busy, await ingestion.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
                await using var check = await factory.CreateDbContextAsync(token);
                Assert.AreEqual(0, await check.MetricPoints.CountAsync(token));
                await transaction.CommitAsync(token);
            }
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingestion.WriteAsync(source.Id, resource, new ScopeMetrics(), metric, token));
            await using var verify = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
        }, context.CancellationToken);
}
