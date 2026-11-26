extern alias ObservabilityHost;
extern alias ResourcesHost;
extern alias OperationsHost;
extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Exceptions;
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
using OpenTelemetry.Metrics;
using Metric = OpenTelemetry.Proto.Metrics.V1.Metric;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Vigiloom.Modules.AlertingOperations.Domain;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
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
            var temporalAddress = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
            var taskQueue = "vigiloom-req48-test-" + Guid.CreateVersion7().ToString("N");
            var dispatchedIds = new HashSet<Guid>();
            TemporalClient? temporal = null;
            try
            {
            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Resources"] = resourcesDb.ConnectionString, ["Services:Observability:Url"] = "http://127.0.0.1:1", ["Work:ObservabilityQueue"] = taskQueue, ["Work:AuditQueue"] = taskQueue + "-audit" }));
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
                { ["ConnectionStrings:Observability"] = observabilityDb.ConnectionString, ["Services:Resources:Url"] = resourceAddress, ["Services:Operations:Url"] = operationsAddress, ["Work:OperationsQueue"] = taskQueue + "-operations", ["Work:AuditQueue"] = taskQueue + "-operations", ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1" }));
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
            await ProjectAsync(serviceId);
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
            using (var spoofed = await PostExternalMetricsAsync(gatewayClient, externalSource.Secret, token, "Vigiloom.Web"))
            {
                Assert.AreEqual(HttpStatusCode.OK, spoofed.StatusCode);
                Assert.AreEqual(1L, ExportMetricsServiceResponse.Parser.ParseFrom(await spoofed.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedDataPoints);
            }
            using (var pending = await PostExternalMetricsAsync(gatewayClient, externalSource.Secret, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, pending.StatusCode);
                Assert.AreEqual(1L, ExportMetricsServiceResponse.Parser.ParseFrom(await pending.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedDataPoints);
            }
            Guid candidateId;
            await using (var db = await resourceContexts.CreateDbContextAsync(token))
            {
                Assert.IsTrue(await db.ApplicationCandidates.AnyAsync(x => x.SourceId == externalSource.SourceId && x.ServiceKey == "Vigiloom.Web", token));
                var candidate = await db.ApplicationCandidates.SingleAsync(x => x.SourceId == externalSource.SourceId && x.ServiceKey == "External.App", token);
                candidateId = candidate.Id;
            }
            Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
            using var approveResponse = await resourcesClient.PostAsJsonAsync($"/internal/v1/application-candidates/{candidateId:D}/approve",
                new ApplicationApprovalRequest(Guid.CreateVersion7()), token);
            approveResponse.EnsureSuccessStatusCode();
            var approval = await approveResponse.Content.ReadFromJsonAsync<ApplicationApprovalResult>(cancellationToken: token);
            Assert.AreEqual(ApplicationApprovalStatus.Approved, approval!.Status);
            await ProjectAsync(approval.ServiceId!.Value);
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
            var idleBuilder = Host.CreateApplicationBuilder();
            idleBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Services:Observability:Url"] = observationAddress, ["Vigiloom:SelfTelemetry:Enabled"] = "true" });
            idleBuilder.AddVigiloomSelfTelemetry("Vigiloom.Worker");
            using (var idleHost = idleBuilder.Build())
            {
                await idleHost.StartAsync(token);
                var metrics = idleHost.Services.GetRequiredService<MeterProvider>();
                Assert.IsTrue(await Task.Run(() => metrics.ForceFlush(10000), token));
                Guid workerServiceId;
                await using (var db = await resourceContexts.CreateDbContextAsync(token))
                {
                    var worker = await db.ApplicationServices.SingleAsync(x => x.ServiceKey == "Vigiloom.Worker", token);
                    var group = await db.Groups.SingleAsync(x => x.Name == "Self", token);
                    Assert.IsTrue(await db.ServiceGroups.AnyAsync(x => x.ServiceId == worker.Id && x.GroupId == group.Id, token));
                    workerServiceId = worker.Id;
                }
                await ProjectAsync(workerServiceId);
                Assert.IsTrue(await Task.Run(() => metrics.ForceFlush(10000), token));
                Assert.IsTrue(await verify.MetricContexts.AnyAsync(row => row.ServiceId == workerServiceId && row.MetricName == "vigiloom.service.running", token));
                await idleHost.StopAsync(token);
            }
            using var versionResponse = await client.PostAsJsonAsync("/internal/v1/public-observation/versions", new[] { serviceId }, token);
            versionResponse.EnsureSuccessStatusCode();
            var versions = await versionResponse.Content.ReadFromJsonAsync<PublicObservationVersions[]>(cancellationToken: token);
            Assert.IsNotNull(versions);
            Assert.AreEqual(serviceId, versions.Single().Id);
            Assert.HasCount(8, versions[0].Service);
            Assert.HasCount(8, versions[0].Source);
            using var statuses = await client.PostAsJsonAsync("/internal/v1/public-observation/statuses", new[] { serviceId }, token);
            statuses.EnsureSuccessStatusCode();
            var statusMap = await statuses.Content.ReadFromJsonAsync<Dictionary<Guid, Vigiloom.Modules.ServiceObservability.Application.Health.HealthStatusReader.OverviewStatus>>(cancellationToken: token);
            Assert.AreEqual(ServiceHealthState.Unknown, statusMap![serviceId].Status.State);
            if (!string.IsNullOrWhiteSpace(temporalAddress))
            {
                var targetSchedule = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                await using (var db = new SchedulingDbContext(targetSchedule)) await db.Database.MigrateAsync(token);
                await using var scope = observation.Services.CreateAsyncScope();
                var actor = Guid.CreateVersion7();
                var policy = await scope.ServiceProvider.GetRequiredService<HealthNetworkPolicyAdministration>()
                    .UpdateAsync(new(["127.0.0.0/8"], ""), actor, token);
                Assert.IsNotNull(policy);
                var target = await scope.ServiceProvider.GetRequiredService<HealthTargetAdministration>()
                    .CreateAsync(new(serviceId, null, new("http://127.0.0.1:12345/health", 30, 5, 1, 1, true, "")), actor, token);
                Assert.AreEqual(HealthTargetStatus.Saved, target.Status);
                var store = new HealthStore(observationContexts, observation.Services.GetServices<IHealthTransitionSink>());
                var lease = Assert.ContainsSingle(await store.ClaimAsync(1, token));
                var work = await store.LoadAsync(lease, token);
                var network = await store.NetworkAsync(token);
                Assert.IsNotNull(work);
                Assert.IsNotNull(network);
                Assert.IsTrue(await store.CommitAsync(work, network, HealthObservationKind.Unhealthy, await store.NowAsync(token), token));

                var ids = new List<Guid>();
                await using (var connection = new SqlConnection(observabilityDb.ConnectionString))
                {
                    await connection.OpenAsync(token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT MessageId FROM scheduling.Outbox";
                    await using var records = await command.ExecuteReaderAsync(token);
                    while (await records.ReadAsync(token)) ids.Add(records.GetGuid(0));
                }
                foreach (var id in ids) dispatchedIds.Add(id);
                temporal ??= await TemporalClient.ConnectAsync(new(temporalAddress));
                var workClient = new ServiceWorkClient(temporal);
                var handler = new OperationsHost::Vigiloom.Operations.Work.OperationsWorkHandler(
                    operations.Services.GetRequiredService<IConfiguration>(), new NotificationWriter(), new OperationAuditWriter(), new TimelineWriter());
                var dispatcher = new OutboxDispatcher(observabilityDb.ConnectionString, [new TemporalOutboxHandler(workClient)], NullLogger<OutboxDispatcher>.Instance);
                using var worker = ServiceWorkWorker.Create(temporal, taskQueue + "-operations", handler);
                await worker.ExecuteAsync(async () =>
                {
                    await dispatcher.DispatchAsync(token);
                    foreach (var id in ids) await workClient.Get(id.ToString("D")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
                }, token);
                await using var saved = await operationContexts.CreateDbContextAsync(token);
                Assert.IsTrue(await saved.Set<ServiceHealthAlert>().AnyAsync(row => row.ServiceId == serviceId && row.RecoveredAt == null, token));
            }
            async Task ProjectAsync(Guid id)
            {
                await using var connection = new SqlConnection(resourcesDb.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MessageId, Payload FROM scheduling.Outbox ORDER BY RowId";
                Guid messageId = default;
                ServiceWork? work = null;
                await using (var records = await command.ExecuteReaderAsync(token))
                    while (await records.ReadAsync(token))
                    {
                        var rowId = records.GetGuid(0);
                        var candidate = JsonSerializer.Deserialize<TemporalWorkRequest>(records.GetString(1))!.Work;
                        if (!string.IsNullOrWhiteSpace(temporalAddress)) dispatchedIds.Add(rowId);
                        if (candidate.Kind == nameof(ApplicationApproved) && candidate.Payload.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))
                        { messageId = rowId; work = candidate; }
                    }
                Assert.IsNotNull(work);
                await using var scope = observation.Services.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<IServiceWorkHandler>();
                if (string.IsNullOrWhiteSpace(temporalAddress)) { await handler.ExecuteAsync(work, token); return; }
                temporal ??= await TemporalClient.ConnectAsync(new(temporalAddress));
                var workClient = new ServiceWorkClient(temporal);
                var dispatcher = new OutboxDispatcher(resourcesDb.ConnectionString, [new TemporalOutboxHandler(workClient)], NullLogger<OutboxDispatcher>.Instance);
                using var worker = ServiceWorkWorker.Create(temporal, taskQueue, handler);
                await worker.ExecuteAsync(async () =>
                {
                    await dispatcher.DispatchAsync(token);
                    await workClient.Get(messageId.ToString("D")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
                }, token);
            }
            using var anonymous = await client.GetAsync($"/api/v1/service-health/{serviceId:D}/status", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var internalStatus = await client.GetAsync($"/internal/v1/health/{serviceId:D}/status", token);
            Assert.AreEqual(HttpStatusCode.OK, internalStatus.StatusCode);
            }
            finally
            {
                if (temporal is not null && dispatchedIds.Count > 0)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    foreach (var id in dispatchedIds)
                    {
                        try
                        {
                            await temporal.WorkflowService.DeleteWorkflowExecutionAsync(new()
                            { Namespace = "default", WorkflowExecution = new() { WorkflowId = id.ToString("D") } },
                                new RpcOptions { CancellationToken = cleanup.Token });
                        }
                        catch (RpcException error) when (error.Code == RpcException.StatusCode.NotFound) { }
                    }
                }
            }
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

    private static async Task<HttpResponseMessage> PostExternalMetricsAsync(HttpClient client, string secret, CancellationToken token, string name = "External.App")
    {
        var resource = ExternalResource(name);
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

    private static Resource ExternalResource(string name = "External.App")
    {
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } });
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
