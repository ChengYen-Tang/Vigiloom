extern alias ObservabilityHost;

using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
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
