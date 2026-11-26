extern alias ObservabilityHost;
extern alias ResourcesHost;
extern alias OperationsHost;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SelfTelemetryIntegrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task RegistersSelfAndAcceptsTelemetryAfterProjection() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourcesDb, outer) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (observabilityDb, middle) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, token) =>
        {
            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Resources"] = resourcesDb.ConnectionString, ["Services:Observability:Url"] = "http://127.0.0.1:1" }));
                web.ConfigureTestServices(services =>
                {
                    RemoveWorkers(services);
                    services.RemoveAll<IDbContextFactory<ResourcesDbContext>>();
                    services.RemoveAll<DbContextOptions<ResourcesDbContext>>();
                    services.AddPooledDbContextFactory<ResourcesDbContext>(options => options.UseSqlServer(resourcesDb.ConnectionString,
                        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")));
                    services.RemoveAll<IOutboxHandler>();
                    services.AddScoped<IOutboxHandler, NoopOutboxHandler>();
                });
            });
            resources.UseKestrel(0);
            using var resourcesClient = resources.CreateClient();
            var resourceContexts = resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resourceContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var scheduleOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(resourcesDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(scheduleOptions)) await db.Database.MigrateAsync(token);
            var resourceAddress = resources.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            using var operations = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableOperationsWorker", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services => RemoveWorkers(services));
            });
            operations.UseKestrel(0);
            using var operationsClient = operations.CreateClient();
            var operationContexts = operations.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            var logContexts = operations.Services.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>();
            var quotaContexts = operations.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            await using (var db = await operationContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await logContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await quotaContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var operationsAddress = operations.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            using var observation = new WebApplicationFactory<ObservabilityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseEnvironment("Production");
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Observability"] = observabilityDb.ConnectionString, ["Services:Resources:Url"] = resourceAddress, ["Services:Operations:Url"] = operationsAddress }));
                web.ConfigureTestServices(services =>
                {
                    RemoveWorkers(services);
                    services.RemoveAll<IDbContextFactory<ServiceObservabilityDbContext>>();
                    services.RemoveAll<DbContextOptions<ServiceObservabilityDbContext>>();
                    services.AddPooledDbContextFactory<ServiceObservabilityDbContext>(options => options.UseSqlServer(observabilityDb.ConnectionString,
                        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")));
                });
            });
            observation.UseKestrel(0);
            using var client = observation.CreateClient();
            var observationContexts = observation.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await observationContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var traceQuotaContexts = observation.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            await using (var db = await traceQuotaContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var observationAddress = observation.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            resources.Services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] = observationAddress;

            using (var initial = await PostMetricsAsync(client, token)) Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode);
            Guid serviceId;
            await using (var db = await resourceContexts.CreateDbContextAsync(token))
            {
                var service = await db.ApplicationServices.SingleAsync(token);
                serviceId = service.Id;
                var group = await db.Groups.SingleAsync(x => x.Name == "Self", token);
                Assert.IsTrue(await db.ServiceGroups.AnyAsync(x => x.ServiceId == service.Id && x.GroupId == group.Id, token));
            }
            await using (var schedule = new SchedulingDbContext(scheduleOptions))
            {
                var payload = await schedule.Database.SqlQueryRaw<string>("SELECT TOP (1) Payload AS [Value] FROM scheduling.Outbox ORDER BY RowId").SingleAsync(token);
                var work = JsonSerializer.Deserialize<TemporalWorkRequest>(payload)!.Work;
                await using var scope = observation.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IServiceWorkHandler>().ExecuteAsync(work, token);
            }
            using (var accepted = await PostMetricsAsync(client, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                var body = ExportMetricsServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token));
                Assert.IsNull(body.PartialSuccess);
            }
            var nanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var resource = SelfResource();
            var span = new Span { Name = "self.test.span", TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()),
                SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray()), StartTimeUnixNano = nanos, EndTimeUnixNano = nanos };
            var traceRequest = new ExportTraceServiceRequest { ResourceSpans = { new ResourceSpans { Resource = resource,
                ScopeSpans = { new ScopeSpans { Spans = { span } } } } } };
            using (var traceBody = new ByteArrayContent(traceRequest.ToByteArray()))
            {
                traceBody.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var trace = await client.PostAsync("/internal/v1/self-otlp/v1/traces", traceBody, token);
                Assert.AreEqual(HttpStatusCode.OK, trace.StatusCode);
                Assert.IsNull(ExportTraceServiceResponse.Parser.ParseFrom(await trace.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            var logRequest = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = resource,
                ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord { TimeUnixNano = nanos, Body = new AnyValue { StringValue = "self test" } } } } } } } };
            using (var logBody = new ByteArrayContent(logRequest.ToByteArray()))
            {
                logBody.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var log = await client.PostAsync("/internal/v1/self-otlp/v1/logs", logBody, token);
                Assert.AreEqual(HttpStatusCode.OK, log.StatusCode);
                Assert.IsNull(ExportLogsServiceResponse.Parser.ParseFrom(await log.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            await using (var logs = await logContexts.CreateDbContextAsync(token)) Assert.AreEqual(1, await logs.LogRecords.CountAsync(token));
            await using var verify = await observationContexts.CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
            Assert.AreEqual(1, await verify.TraceSpans.CountAsync(token));
            using var anonymous = await client.GetAsync($"/api/v1/service-health/{serviceId:D}/status", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var internalStatus = await client.GetAsync($"/internal/v1/health/{serviceId:D}/status", token);
            Assert.AreEqual(HttpStatusCode.OK, internalStatus.StatusCode);
        }, middle), outer), context.CancellationToken);

    private static async Task<HttpResponseMessage> PostMetricsAsync(HttpClient client, CancellationToken token)
    {
        var resource = SelfResource();
        var metric = new Metric { Name = "self.test", Gauge = new Gauge { DataPoints = { new NumberDataPoint
            { TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000, AsDouble = 1 } } } };
        var request = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics
            { Resource = resource, ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
        using var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync("/internal/v1/self-otlp/v1/metrics", body, token);
    }

    private static Resource SelfResource()
    {
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "Vigiloom.Web" } });
        resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "Production" } });
        return resource;
    }

    private static void RemoveWorkers(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
            (item.ImplementationType?.Name is "HealthWorker" or "HealthPolicyWorker" or "HealthExpiryWorker" or "WorkDispatcher" or "LineAggregationWorker" ||
             item.ImplementationFactory?.Method.Name.Contains("AddHostedTemporalWorker", StringComparison.Ordinal) == true)).ToArray())
            services.Remove(descriptor);
    }

    private sealed class NoopOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
