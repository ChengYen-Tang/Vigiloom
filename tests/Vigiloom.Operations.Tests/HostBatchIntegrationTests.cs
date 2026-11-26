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
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Authentication;
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
                db.Set<AgentSetting>().Add(new AgentSetting { HeartbeatSeconds = 120, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now });
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
            var targetUpdate = new RecordedAgentUpdateTarget(Guid.CreateVersion7(), identity.AgentId, Guid.CreateVersion7(), "1.2.3", "win-x64", new string('a', 64), DateTimeOffset.UtcNow);
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
                    configured.RemoveAll<IRecordedAgentTargetReader>();
                    configured.AddSingleton<IRecordedAgentTargetReader>(new TargetReader(targetUpdate));
                    configured.RemoveAll<IHostLogSink>();
                    configured.AddSingleton<IHostLogSink>(sink);
                });
            });
            using var client = host.CreateClient();
            using (var unauthorizedSettings = await client.GetAsync("/api/v1/agent-management/settings/platform", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedSettings.StatusCode);
            using (var unauthorizedHeartbeat = await client.PostAsync("/api/v1/agent/heartbeat", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedHeartbeat.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            using (var settings = await client.GetAsync("/api/v1/agent/settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
                var snapshot = await settings.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(120, snapshot!.Settings.HeartbeatSeconds);
                using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/settings");
                unchangedRequest.Headers.IfNoneMatch.Add(settings.Headers.ETag!);
                using var unchanged = await client.SendAsync(unchangedRequest, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var epoch = Guid.CreateVersion7();
            using (var session = await client.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, epoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, session.StatusCode);
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 1, null), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var update = new AgentUpdateResult(Guid.CreateVersion7(), targetUpdate.UpdateId, targetUpdate.PackageId, targetUpdate.ManifestSha256, 1,
                AgentUpdateResultKind.Succeeded, DateTimeOffset.UtcNow, false, null);
            using (var result = await client.PostAsJsonAsync("/api/v1/agent/update-results", update, token))
                Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            var updateStatus = await client.GetFromJsonAsync<AgentVersionObservation>($"/internal/v1/agents/{identity.AgentId:D}/update-status", token);
            Assert.AreEqual("1.2.3", updateStatus!.LatestResult!.PackageVersion);
            Assert.AreEqual(epoch, (await client.GetFromJsonAsync<AgentQueueSession>("/api/v1/agent/queue-session", token))!.Epoch);
            var capacityCommand = new AgentQueueCapacityCommand(new AgentQueueCapacityUpdate(new AgentQueueCapacityOverrides(128L * 1024 * 1024, null), ""), Guid.CreateVersion7());
            using (var updated = await client.PostAsJsonAsync($"/internal/v1/agent-queue-capacity/devices/{identity.DeviceId:D}", capacityCommand, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            var capacity = await client.GetFromJsonAsync<AgentQueueCapacitySnapshot>("/api/v1/agent/queue-capacity", token);
            Assert.AreEqual(128L * 1024 * 1024, capacity!.Capacity.PayloadBytes);
            var queue = new AgentQueueSnapshot(0, 0, 0, capacity.Capacity.PayloadBytes, capacity.Capacity.BatchCount);
            var capacityReport = new AgentQueueCapacityReport(capacity, DateTimeOffset.UtcNow, null);
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 2, queue, CapacityConfiguration: capacityReport, Runtime: new("1.2.3", "win-x64")), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            using (var usage = await client.PostAsJsonAsync("/internal/v1/agent-package-usage", new AgentPackageUsageRequest("1.2.3", "win-x64"), token))
                Assert.IsTrue((await usage.Content.ReadFromJsonAsync<AgentPackageUsageResponse>(cancellationToken: token))!.InUse);
            using (var usage = await client.PostAsJsonAsync("/internal/v1/agent-package-usage", new AgentPackageUsageRequest("9.9.9", "win-x64"), token))
                Assert.IsFalse((await usage.Content.ReadFromJsonAsync<AgentPackageUsageResponse>(cancellationToken: token))!.InUse);
            var command = new AgentSettingsCommand(new AgentSettingsUpdate(new AgentSettingsOverrides(90, null, null), ""), Guid.CreateVersion7());
            using (var updated = await client.PostAsJsonAsync($"/internal/v1/agent-settings/devices/{identity.DeviceId:D}", command, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            using (var effective = await client.GetAsync("/api/v1/agent/settings", token))
                Assert.AreEqual(90, (await effective.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token))!.Settings.HeartbeatSeconds);
            client.DefaultRequestHeaders.Authorization = null;
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
            Assert.AreEqual(120, (await check.AgentStatuses.SingleAsync(token)).HeartbeatSafetyIntervalSeconds);
            Assert.IsFalse(await check.ReceivedBatches.AnyAsync(x => EF.Property<bool>(x, "PendingDelivery"), token));
            Assert.AreEqual(1, await check.HostMetrics.CountAsync(token));
            Assert.AreEqual(2, sink.Calls);

            using var proxiedHost = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString,
                    ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1"
                }));
                web.ConfigureTestServices(configured =>
                {
                    configured.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    configured.RemoveAll<IApiCredentialValidation>();
                    configured.AddSingleton<IApiCredentialValidation, CredentialValidation>();
                });
            });
            proxiedHost.UseKestrel(0);
            using var proxyClient = proxiedHost.CreateClient();
            proxyClient.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            proxyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
            using (var response = await proxyClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/agent-settings", token))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var status = await proxyClient.GetFromJsonAsync<AgentConnectionStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-status", token);
            Assert.AreEqual(AgentConnectionState.Online, status!.State);
            var settingsStatus = await proxyClient.GetFromJsonAsync<AgentSettingsStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-settings-status", token);
            Assert.AreEqual(AgentSettingsApplyState.WaitingForReport, settingsStatus!.State);
            var capacityStatus = await proxyClient.GetFromJsonAsync<AgentQueueCapacityStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-queue-capacity-status", token);
            Assert.AreEqual(AgentSettingsApplyState.Applied, capacityStatus!.State);
            using (var response = await proxyClient.PostAsJsonAsync("/internal/v1/agent-connections", new[] { identity.DeviceId }, token))
            {
                response.EnsureSuccessStatusCode();
                var connections = await response.Content.ReadFromJsonAsync<Dictionary<Guid, AgentConnectionStatus>>(cancellationToken: token);
                Assert.AreEqual(AgentConnectionState.Online, connections![identity.DeviceId].State);
            }
            using (var response = await proxyClient.GetAsync("/api/v1/agent-management/settings/platform", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
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
            if (request.RequestUri!.AbsolutePath.EndsWith("/agents/active", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { identity.AgentId }) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/agent-overview", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new DeviceAgentOverview(
                    new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true),
                    new AgentRegistrationSummary(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null)) }) };
            if (request.Method == HttpMethod.Get)
            {
                var payload = request.RequestUri!.AbsolutePath.EndsWith("/agents", StringComparison.Ordinal)
                    ? JsonContent.Create(new DeviceAgentRegistrations([new(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null)], [], identity.DeviceGeneration))
                    : JsonContent.Create(new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true));
                return new(HttpStatusCode.OK) { Content = payload };
            }
            var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            return command?.Credential == "valid" ? new(HttpStatusCode.OK) { Content = JsonContent.Create(identity) } : new(HttpStatusCode.Unauthorized);
        }
    }

    private sealed class CredentialValidation : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult<VerifiedApiCredentialResponse?>(value == "valid" ? new(Guid.CreateVersion7(), "test", false) : null);
    }

    private sealed class TargetReader(RecordedAgentUpdateTarget target) : IRecordedAgentTargetReader
    {
        public Task<RecordedAgentUpdateTarget?> GetAsync(Guid agentId, Guid updateId, CancellationToken token) =>
            Task.FromResult<RecordedAgentUpdateTarget?>(target.AgentId == agentId && target.UpdateId == updateId ? target : null);
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
