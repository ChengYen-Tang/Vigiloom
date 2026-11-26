extern alias ObservabilityHost;

using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class ObservabilityOtlpErrorsTests(TestContext context)
{
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    public Task InternalTelemetryCreatesOneSharedSource() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = new WebApplicationFactory<ObservabilityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Observability"] = database.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    RemoveWorkers(services);
                    services.RemoveAll<IDbContextFactory<ServiceObservabilityDbContext>>();
                    services.RemoveAll<DbContextOptions<ServiceObservabilityDbContext>>();
                    services.AddPooledDbContextFactory<ServiceObservabilityDbContext>(options => options.UseSqlServer(database.ConnectionString,
                        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")));
                });
            });
            using var client = host.CreateClient();
            var contexts = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            using var firstBody = new ByteArrayContent([]);
            using var secondBody = new ByteArrayContent([]);
            var results = await Task.WhenAll(client.PostAsync("/internal/v1/self-otlp/v1/metrics", firstBody, token),
                client.PostAsync("/internal/v1/self-otlp/v1/metrics", secondBody, token));
            foreach (var result in results)
            {
                Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, result.StatusCode);
                result.Dispose();
            }
            await using var verify = await contexts.CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.Sources.CountAsync(source => source.IsSelf, token));
        }, context.CancellationToken);

    [TestMethod]
    public async Task AuthenticationFailuresUseOtlpResponses()
    {
        using var host = new WebApplicationFactory<ObservabilityHost::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("Hangfire:Enabled", "false");
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Observability"] = "Server=127.0.0.1,1;Database=Vigiloom_Unavailable;User Id=test;Password=test;Connect Timeout=1;TrustServerCertificate=true"
            }));
            web.ConfigureTestServices(services =>
            {
                RemoveWorkers(services);
                services.RemoveAll<IDbContextFactory<ServiceObservabilityDbContext>>();
                services.RemoveAll<DbContextOptions<ServiceObservabilityDbContext>>();
                services.AddPooledDbContextFactory<ServiceObservabilityDbContext>(options => options.UseSqlServer(
                    "Server=127.0.0.1,1;Database=Vigiloom_Unavailable;User Id=test;Password=test;Connect Timeout=1;TrustServerCertificate=true"));
            });
        });
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var telemetry = await client.GetAsync($"/api/v1/service-telemetry/{Guid.CreateVersion7():D}/logs?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z", context.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, telemetry.StatusCode);
        Assert.IsTrue(telemetry.Headers.CacheControl?.NoStore);
        using var missing = await client.PostAsync("/api/v1/otlp/v1/metrics", new ByteArrayContent([]), context.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.AreEqual("application/x-protobuf", missing.Content.Headers.ContentType?.MediaType);

        var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vls_{Guid.CreateVersion7():D}.{secret}");
        using var unavailable = await client.PostAsync("/api/v1/otlp/v1/metrics", new ByteArrayContent([]), context.CancellationToken);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.AreEqual("application/x-protobuf", unavailable.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    public Task RetentionEndpointsUseTheObservationPolicyAndOperationsCount() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var audit = new RecordingAudit();
            using var host = new WebApplicationFactory<ObservabilityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Observability"] = database.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    RemoveWorkers(services);
                    services.RemoveAll<IDbContextFactory<ServiceObservabilityDbContext>>();
                    services.RemoveAll<DbContextOptions<ServiceObservabilityDbContext>>();
                    services.AddPooledDbContextFactory<ServiceObservabilityDbContext>(options => options.UseSqlServer(database.ConnectionString,
                        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")));
                    services.RemoveAll<IApplicationLogRetentionCounter>();
                    services.AddSingleton<IApplicationLogRetentionCounter, LogCount>();
                    services.RemoveAll<IOperationAuditWriter<OperationAudit>>();
                    services.AddSingleton<IOperationAuditWriter<OperationAudit>>(audit);
                });
            });
            using var client = host.CreateClient();
            var contexts = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var quotaContexts = host.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            await using (var quotaDb = await quotaContexts.CreateDbContextAsync(token)) await quotaDb.Database.MigrateAsync(token);
            var traceQuota = await client.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/internal/v1/trace-quota", token);
            Assert.IsNotNull(traceQuota);
            using var updateTrace = await client.PutAsJsonAsync("/internal/v1/trace-quota", new UpdateTraceQuotaCommand(
                traceQuota with { SourceRate = 25, SourceBurst = 50, GlobalRate = 100, GlobalBurst = 200 }, Guid.CreateVersion7()), token);
            updateTrace.EnsureSuccessStatusCode();
            Assert.AreEqual(25, (await client.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/internal/v1/trace-quota", token))!.SourceRate);
            var policy = await client.GetFromJsonAsync<TelemetryRetentionSnapshot>("/internal/v1/telemetry-retention", token);
            Assert.IsNotNull(policy);
            var actor = Guid.CreateVersion7();
            var previewRequest = new TelemetryRetentionPreviewRequest(45, 7, policy.Version);
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/telemetry-retention/preview", new TelemetryRetentionPreviewCommand(actor, previewRequest), token);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<TelemetryRetentionPreview>(cancellationToken: token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(23L, preview.Logs);
            using var update = await client.PutAsJsonAsync("/internal/v1/telemetry-retention", new TelemetryRetentionUpdateCommand(actor,
                new TelemetryRetentionInput(45, 7, policy.Version, preview.ConfirmationToken)), token);
            update.EnsureSuccessStatusCode();
            Assert.AreEqual(45, (await client.GetFromJsonAsync<TelemetryRetentionSnapshot>("/internal/v1/telemetry-retention", token))!.LogDays);
            Assert.AreEqual(actor, audit.Actor);
            using var issue = await client.PostAsJsonAsync("/internal/v1/observation-sources", new IssueServiceSourceCommand("Example", actor), token);
            issue.EnsureSuccessStatusCode();
            var credential = await issue.Content.ReadFromJsonAsync<SourceCredentialIssue>(cancellationToken: token);
            Assert.IsNotNull(credential);
            var sources = await client.GetFromJsonAsync<ServiceSourceSummary[]>("/internal/v1/observation-sources", token);
            var source = sources!.Single(item => item.Id == credential.SourceId);
            using var rotate = await client.PostAsJsonAsync($"/internal/v1/observation-sources/{source.Id:D}/rotate",
                new RotateServiceSourceCommand(Convert.FromBase64String(source.Version), actor), token);
            rotate.EnsureSuccessStatusCode();
            var rotated = await rotate.Content.ReadFromJsonAsync<SourceRotationResult>(cancellationToken: token);
            Assert.AreEqual(SourceRotationStatus.Rotated, rotated!.Status);
            using var scope = host.Services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            Assert.IsNull(await auth.AuthenticateVersionedAsync(credential.Secret, token));
            Assert.IsNotNull(await auth.AuthenticateVersionedAsync(rotated.Credential, token));
            var now = DateTimeOffset.UtcNow;
            var monitor = new MonitoredService(new ServiceCandidate(source.Id, "collector", "prod", "Collector", now), now);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                db.Services.Add(monitor);
                await db.SaveChangesAsync(token);
            }
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var admitted = await TraceAdmission.AdmitAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
                    [new TraceSource(source.Id, monitor.Id, 1)], token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, admitted.Status);
                await tx.CommitAsync(token);
            }
            var networks = await client.GetFromJsonAsync<HealthNetworkPolicySnapshot>("/internal/v1/health-admin/networks", token);
            Assert.IsNotNull(networks);
            using var saveNetworks = await client.PutAsJsonAsync("/internal/v1/health-admin/networks",
                new UpdateHealthNetworkCommand(new HealthNetworkPolicyInput(["10.0.0.0/8"], networks.Version), actor), token);
            saveNetworks.EnsureSuccessStatusCode();
            var savedNetworks = await saveNetworks.Content.ReadFromJsonAsync<HealthNetworkPolicySnapshot>(cancellationToken: token);
            Assert.Contains("10.0.0.0/8", savedNetworks!.Networks);
            var targetInput = new HealthTargetInput("https://example.test/health", 30, 5, 3, 2, false, "");
            using var createTarget = await client.PostAsJsonAsync("/internal/v1/health-admin/targets",
                new CreateHealthTargetCommand(new CreateHealthTargetInput(monitor.Id, null, targetInput), actor), token);
            createTarget.EnsureSuccessStatusCode();
            var healthTarget = await createTarget.Content.ReadFromJsonAsync<HealthTargetResult>(cancellationToken: token);
            Assert.AreEqual(HealthTargetStatus.Saved, healthTarget!.Status);
            var healthTargets = await client.GetFromJsonAsync<HealthTargetSnapshot[]>($"/internal/v1/health-admin/targets?serviceId={monitor.Id:D}&page=1", token);
            Assert.AreEqual(healthTarget.Target!.Id, healthTargets!.Single().Id);
            var instancePolicy = await client.GetFromJsonAsync<InstancePolicySnapshot>("/internal/v1/instance-admin/policy", token);
            Assert.IsNotNull(instancePolicy);
            using var savePolicy = await client.PutAsJsonAsync("/internal/v1/instance-admin/policy",
                new UpdateInstancePolicyCommand(new InstancePolicyInput(instancePolicy.PerService, instancePolicy.Total, instancePolicy.Version), actor), token);
            savePolicy.EnsureSuccessStatusCode();
            using var register = await client.PostAsJsonAsync("/internal/v1/collectors", new RegisterCollectorCommand("Collector", monitor.Id, true, actor), token);
            register.EnsureSuccessStatusCode();
            var registered = await register.Content.ReadFromJsonAsync<CollectorChangeResult>(cancellationToken: token);
            Assert.AreEqual(CollectorChangeStatus.Updated, registered!.Status);
            using var targetIssue = await client.PostAsJsonAsync("/internal/v1/observation-sources", new IssueServiceSourceCommand("Target", actor), token);
            var target = await targetIssue.Content.ReadFromJsonAsync<SourceCredentialIssue>(cancellationToken: token);
            var collector = await client.GetFromJsonAsync<CollectorSummary>($"/internal/v1/collectors/{registered.CollectorId:D}", token);
            Assert.IsNotNull(collector);
            using var addRoute = await client.PostAsJsonAsync($"/internal/v1/collectors/{collector.Id:D}/routes",
                new SetCollectorRouteCommand(target!.SourceId, collector.Version, "exporter", true, actor), token);
            Assert.AreEqual(CollectorChangeStatus.Updated, await addRoute.Content.ReadFromJsonAsync<CollectorChangeStatus>(cancellationToken: token));
            var routes = await client.GetFromJsonAsync<CollectorRouteSummary[]>($"/internal/v1/collectors/{collector.Id:D}/routes", token);
            Assert.AreEqual(target.SourceId, routes!.Single().SourceId);
            using var revoke = await client.PostAsJsonAsync($"/internal/v1/observation-sources/{source.Id:D}/revoke", new RevokeServiceSourceCommand(actor), token);
            Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
            Assert.IsNull(await auth.AuthenticateVersionedAsync(rotated.Credential, token));
        }, context.CancellationToken);

    private sealed class LogCount : IApplicationLogRetentionCounter
    {
        public Task<long> CountBeforeAsync(DateTimeOffset before, CancellationToken token) => Task.FromResult(23L);
    }

    private sealed class RecordingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Guid Actor { get; private set; }
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
        {
            Actor = audit.ActorId;
            return Task.CompletedTask;
        }
    }

    private static void RemoveWorkers(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
            (item.ImplementationType?.Name is "HealthWorker" or "HealthPolicyWorker" or "HealthExpiryWorker" or "WorkDispatcher" ||
             item.ImplementationFactory?.Method.Name.Contains("AddHostedTemporalWorker", StringComparison.Ordinal) == true)).ToArray())
            services.Remove(descriptor);
    }
}
