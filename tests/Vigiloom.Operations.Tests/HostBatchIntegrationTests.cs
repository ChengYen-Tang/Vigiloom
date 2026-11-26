extern alias HostMonitoring;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostBatchIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task AuthenticatesAndRetriesAfterOperationsCommitted() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, token) =>
        {
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                var policy = new AgentReplayPolicy();
                policy.InitializeFresh(now);
                policy.BeginCleanupCutover(now);
                db.ReplayPolicies.Add(policy);
                await db.SaveChangesAsync(token);
            }
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(operationsDb.ConnectionString, "operations");
            services.AddModuleSqlServer<LogAdmissionDbContext>(operationsDb.ConnectionString, "logquota");
            services.AddModuleSqlServer<CentralLogsDbContext>(operationsDb.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            services.AddModuleSqlServer<SchedulingDbContext>(operationsDb.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(), token);
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(configured =>
                {
                    HostWorkers.RemoveBackgroundWork(configured);
                    configured.RemoveAll<IOutboxHandler>();
                    configured.AddScoped<IOutboxHandler, TestOutboxHandler>();
                });
            });
            using var operationsClient = operations.CreateClient();
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var sink = new CommitThenLoseResponse(operationsClient);
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString }));
                web.ConfigureTestServices(configured =>
                {
                    configured.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    configured.RemoveAll<IHostLogSink>();
                    configured.AddSingleton<IHostLogSink>(sink);
                });
            });
            using var client = host.CreateClient();
            var nowUtc = DateTimeOffset.UtcNow;
            var metric = new AgentMetricPoint(Guid.CreateVersion7(), nowUtc, AgentMetricKind.CpuTotalPercent, "cpu", null, 25);
            var metricBatch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc, [metric]);
            var metricPayload = AgentMetricBatchCodec.Serialize(metricBatch);
            using (var compressed = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true)) await gzip.WriteAsync(metricPayload, token);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(compressed.ToArray()) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content.Headers.ContentEncoding.Add("gzip");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
            var item = new AgentLogRecord(Guid.CreateVersion7(), nowUtc, "host event", null, null, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc,
                Guid.CreateVersion7(), AgentLogSourceKind.Journal, "System", 1, [item]);
            var payload = AgentLogBatchCodec.Serialize(batch);
            using (var unauthorized = await SendAsync(client, payload, null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var trailingSlash = await client.PostAsync("/api/v1/agent/batches/", new ByteArrayContent(payload), token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, trailingSlash.StatusCode);
            using (var lost = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
            using (var repeated = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, repeated.StatusCode);
            using (var already = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, already.StatusCode);
            await using var logs = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await logs.HostLogs.CountAsync(token));
            await using var check = new HostMonitoringDbContext(hostOptions);
            Assert.IsFalse(await check.ReceivedBatches.AnyAsync(x => EF.Property<bool>(x, "PendingDelivery"), token));
            Assert.AreEqual(1, await check.HostMetrics.CountAsync(token));
            Assert.AreEqual(2, sink.Calls);
        }, outerToken), context.CancellationToken);

    private static async Task MigrateAsync<T>(IDbContextFactory<T> factory, CancellationToken token) where T : DbContext
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.MigrateAsync(token);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, byte[] payload, string? credential, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request, token);
    }

    private sealed class IdentityHandler(RegisteredAgentIdentity identity) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            return command?.Credential == "valid" ? new(HttpStatusCode.OK) { Content = JsonContent.Create(identity) } : new(HttpStatusCode.Unauthorized);
        }
    }

    private sealed class CommitThenLoseResponse(HttpClient client) : IHostLogSink
    {
        public int Calls { get; private set; }
        public async Task<LogReceipt> ReceiveAsync(RegisteredAgentIdentity identity, AgentLogBatch batch, CancellationToken token)
        {
            Calls++;
            using var response = await client.PostAsJsonAsync("/internal/v1/host-logs",
                new HostLogSubmission(identity.AgentId, identity.DeviceId, identity.DeviceGeneration, batch), token);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<LogReceipt>(cancellationToken: token);
            return Calls == 1 ? new(LogReceiptStatus.Busy) : receipt!;
        }
    }

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
