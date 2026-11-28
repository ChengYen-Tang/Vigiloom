using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DistributedAgentSiteTests(TestContext context)
{
    [TestMethod]
    public Task AgentEnrollmentAndSettingsCrossRealServices() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourcesDatabase, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDatabase, middleToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDatabase, token) =>
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            using var releases = new HttpClient();
            using var site = await DistributedAgentSite.StartAsync(resourcesDatabase.ConnectionString, hostDatabase.ConnectionString,
                operationsDatabase.ConnectionString, certificate, new AgentTransportGate(),
                new PackageLibraryTransport(Path.Combine(AppContext.BaseDirectory, "unused-test-cache")), new GitHubAgentReleases(releases), token);
            var device = new Device("test", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await site.Resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                db.Devices.Add(device);
                await db.SaveChangesAsync(token);
            }
            await using var scope = site.Resources.Services.CreateAsyncScope();
            var issued = await scope.ServiceProvider.GetRequiredService<AgentRegistration>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(AgentRegistrationStatus.Success, issued.Status);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { BaseAddress = site.GatewayAddress };
            var statePath = Path.Combine(AppContext.BaseDirectory, $"agent-session-{Guid.CreateVersion7():N}");
            try
            {
                var vault = new IdentityVault(statePath);
                var agentId = await new IdentityClient(client).EnrollAsync(vault, site.GatewayAddress, issued.Secret!, token);
                await using (var session = vault.Acquire())
                {
                    var identity = await session.ReadAsync(token);
                    Assert.IsNotNull(identity);
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{agentId:D}.{identity.MachineSecret}");
                }
                using (var transport = new DropFirstSessionResponse(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
                using (var heartbeatHttp = new HttpClient(transport))
                {
                    var heartbeat = new AgentHeartbeatClient(heartbeatHttp);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeat.SendAsync(vault, site.GatewayAddress, token));
                    Assert.IsTrue(transport.Dropped);
                    var firstSession = await client.GetFromJsonAsync<AgentQueueSession>("/api/v1/agent/queue-session", token);
                    Assert.IsNotNull(firstSession?.Epoch);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeat.SendAsync(vault, site.GatewayAddress, token));
                    var retried = await client.GetFromJsonAsync<AgentQueueSession>("/api/v1/agent/queue-session", token);
                    Assert.AreEqual(firstSession.Epoch, retried!.Epoch);
                    var owner = new AgentSettingsOwner(agentId, site.GatewayAddress.AbsoluteUri, vault.StateDirectory);
                    await using var ledger = await MetricLossLedger.StartAsync(vault, owner, token);
                    await ledger.RecordAsync(1, 2, 7, token);
                    var loss = await ledger.ReadAsync(token);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeat.SendAsync(vault, site.GatewayAddress, null, null, ledger.WriterId, token));
                    await using var hostDb = await site.Host.Services.GetRequiredService<IDbContextFactory<HostMonitoringDbContext>>().CreateDbContextAsync(token);
                    var status = await hostDb.AgentStatuses.SingleAsync(x => x.Id == agentId, token);
                    Assert.AreEqual(firstSession.Epoch, status.QueueReportEpoch);
                    Assert.AreEqual(loss.LedgerId, status.MetricLossLedgerId);
                    Assert.AreEqual(7L, status.MetricLossRecords);
                    await ledger.CompleteAsync(token);
                }
                using var settings = await client.GetAsync("/api/v1/agent/settings", token);
                Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
                using var logs = await client.GetAsync("/api/v1/agent/log-settings", token);
                Assert.AreEqual(HttpStatusCode.OK, logs.StatusCode);
                Assert.IsEmpty((await logs.Content.ReadFromJsonAsync<AgentLogSettingsSnapshot>(cancellationToken: token))!.Sources);
                using var host = site.Host.CreateClient();
                var createSource = new HostLogSourceCreateCommand(new(AgentLogSourceKind.TextFile, "test", "/var/log/test", true, AgentTextLogMode.SingleLine), Guid.CreateVersion7());
                using var created = await host.PostAsJsonAsync($"/internal/v1/devices/{device.Id:D}/log-sources", createSource, token);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
                var source = (await created.Content.ReadFromJsonAsync<HostLogConfigurationResult>(cancellationToken: token))!.Source!;
                using var changedLogs = await client.GetAsync("/api/v1/agent/log-settings", token);
                Assert.AreEqual(source.Settings.SourceId, Assert.ContainsSingle((await changedLogs.Content.ReadFromJsonAsync<AgentLogSettingsSnapshot>(cancellationToken: token))!.Sources).SourceId);
                var now = DateTimeOffset.UtcNow;
                var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                    source.Settings.SourceId, AgentLogSourceKind.TextFile, "test", 1,
                    [new AgentLogRecord(Guid.CreateVersion7(), now, "test record", null, null, null, null, null)]);
                using var content = new ByteArrayContent(AgentLogBatchCodec.Serialize(batch));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var received = await client.PostAsync("/api/v1/agent/batches", content, token);
                Assert.AreEqual(HttpStatusCode.NoContent, received.StatusCode);
                await using var logDb = await site.Operations.Services.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
                Assert.IsTrue(await logDb.HostLogs.AnyAsync(x => x.AgentId == agentId && x.BatchId == batch.BatchId && x.Content == "test record", token));
            }
            finally
            {
                var prefix = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(statePath).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated root.");
                if (Directory.Exists(statePath)) Directory.Delete(statePath, recursive: true);
            }
        }, middleToken), outerToken), context.CancellationToken);

    private sealed class DropFirstSessionResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Dropped { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (!Dropped && request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/agent/queue-session" && response.IsSuccessStatusCode)
            {
                Dropped = true;
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after the queue session committed.");
            }
            return response;
        }
    }
}
