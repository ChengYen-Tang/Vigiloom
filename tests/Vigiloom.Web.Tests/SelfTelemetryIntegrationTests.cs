extern alias ObservabilityHost;
extern alias ResourcesHost;
extern alias OperationsHost;
extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;
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
    public Task AcceptsSelfAndApprovedApplicationTelemetryAfterProjection() =>
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
                { ["ConnectionStrings:Observability"] = observabilityDb.ConnectionString, ["Services:Resources:Url"] = resourceAddress, ["Services:Operations:Url"] = operationsAddress, ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1" }));
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
            var observationSchedule = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(observabilityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(observationSchedule)) await db.Database.MigrateAsync(token);
            var observationAddress = observation.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            resources.Services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] = observationAddress;
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:Gateway:SourceIdentityAddress"] = observationAddress,
                    ["ReverseProxy:Clusters:observability:Destinations:observability:Address"] = observationAddress.TrimEnd('/') + "/"
                }));
            });
            using var gatewayClient = gateway.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using (var blocked = await gatewayClient.GetAsync("/internal/v1/self-otlp/v1/metrics", token)) Assert.AreEqual(HttpStatusCode.NotFound, blocked.StatusCode);


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
            SourceCredentialIssue externalSource;
            await using (var scope = observation.Services.CreateAsyncScope())
                externalSource = await scope.ServiceProvider.GetRequiredService<SourceAdministration>().IssueAsync("External", Guid.CreateVersion7(), token);
            using (var pending = await PostExternalMetricsAsync(gatewayClient, externalSource.Secret, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, pending.StatusCode);
                Assert.AreEqual(1L, ExportMetricsServiceResponse.Parser.ParseFrom(await pending.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedDataPoints);
            }
            Guid candidateId;
            await using (var db = await resourceContexts.CreateDbContextAsync(token))
            {
                var candidate = await db.ApplicationCandidates.SingleAsync(x => x.SourceId == externalSource.SourceId, token);
                Assert.AreEqual("External.App", candidate.ServiceKey);
                candidateId = candidate.Id;
            }
            Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
            using var approveResponse = await resourcesClient.PostAsJsonAsync($"/internal/v1/application-candidates/{candidateId:D}/approve",
                new ApplicationApprovalRequest(Guid.CreateVersion7()), token);
            approveResponse.EnsureSuccessStatusCode();
            var approval = await approveResponse.Content.ReadFromJsonAsync<ApplicationApprovalResult>(cancellationToken: token);
            Assert.AreEqual(ApplicationApprovalStatus.Approved, approval!.Status);
            await using (var schedule = new SchedulingDbContext(scheduleOptions))
            {
                var payloads = await schedule.Database.SqlQueryRaw<string>("SELECT Payload AS [Value] FROM scheduling.Outbox ORDER BY RowId").ToArrayAsync(token);
                var work = payloads.Select(payload => JsonSerializer.Deserialize<TemporalWorkRequest>(payload)!.Work)
                    .Single(item => item.Kind == nameof(ApplicationApproved) && item.Payload.Contains(approval.ServiceId!.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                await using var scope = observation.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IServiceWorkHandler>().ExecuteAsync(work, token);
            }
            using (var accepted = await PostExternalMetricsAsync(gatewayClient, externalSource.Secret, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                Assert.IsNull(ExportMetricsServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            Assert.AreEqual(2, await verify.MetricPoints.CountAsync(token));
            var externalNanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var externalTrace = traceRequest.Clone();
            externalTrace.ResourceSpans[0].Resource = ExternalResource();
            externalTrace.ResourceSpans[0].ScopeSpans[0].Spans[0].StartTimeUnixNano = externalNanos;
            externalTrace.ResourceSpans[0].ScopeSpans[0].Spans[0].EndTimeUnixNano = externalNanos;
            using (var accepted = await PostExternalPayloadAsync(gatewayClient, "traces", externalSource.Secret, externalTrace.ToByteArray(), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                Assert.IsNull(ExportTraceServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            var externalLog = logRequest.Clone();
            externalLog.ResourceLogs[0].Resource = ExternalResource();
            externalLog.ResourceLogs[0].ScopeLogs[0].LogRecords[0].TimeUnixNano = externalNanos;
            using (var accepted = await PostExternalPayloadAsync(gatewayClient, "logs", externalSource.Secret, externalLog.ToByteArray(), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                Assert.IsNull(ExportLogsServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            Assert.AreEqual(2, await verify.TraceSpans.CountAsync(token));
            await using (var logs = await logContexts.CreateDbContextAsync(token)) Assert.AreEqual(2, await logs.LogRecords.CountAsync(token));
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

    private static async Task<HttpResponseMessage> PostExternalMetricsAsync(HttpClient client, string secret, CancellationToken token)
    {
        var resource = ExternalResource();
        var metric = new Metric { Name = "external.test", Gauge = new Gauge { DataPoints = { new NumberDataPoint
            { TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000, AsDouble = 2 } } } };
        var payload = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics
            { Resource = resource, ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
        return await PostExternalPayloadAsync(client, "metrics", secret, payload.ToByteArray(), token);
    }

    private static async Task<HttpResponseMessage> PostExternalPayloadAsync(HttpClient client, string signal, string secret, byte[] payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/otlp/v1/{signal}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return await client.SendAsync(request, token);
    }

    private static Resource ExternalResource()
    {
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "External.App" } });
        resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "Production" } });
        return resource;
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
