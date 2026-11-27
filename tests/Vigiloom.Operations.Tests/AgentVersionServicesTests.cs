extern alias HostMonitoring;
extern alias ResourcesHost;
extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Tests.Fixtures;
using Vigiloom.UpdateSecurity.AgentPackages;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentVersionServicesTests(TestContext context)
{
    [TestMethod]
    public Task HostAcceptsUpdateResultForRealResourcesAssignment() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourceDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, token) =>
        {
            var resourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new ResourcesDbContext(resourcesOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions)) await db.Database.MigrateAsync(token);

            var now = DateTimeOffset.UtcNow;
            var device = new Device("server", "server.example", true, now);
            var secret = RandomNumberGenerator.GetBytes(32);
            var agentId = Guid.CreateVersion7();
            byte[] archive = [1, 2, 3, 4];
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.2.3", rid = "win-x64", size = archive.Length, sha256 = archiveHash });
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
            var package = new Vigiloom.Modules.Resources.Domain.AgentPackage { Id = Guid.CreateVersion7(), Version = "1.2.3", RuntimeIdentifier = "win-x64", Sha256 = archiveHash, ManifestSha256 = manifestHash, Size = archive.Length };
            await using (var db = new ResourcesDbContext(resourcesOptions))
            {
                db.Devices.Add(device);
                db.Agents.Add(new RegisteredAgent { Id = agentId, DeviceId = device.Id, DeviceGeneration = device.AgentAuthorizationGeneration, Verifier = SHA256.HashData(secret), CreatedAt = now });
                db.AgentPackages.Add(package);
                await db.SaveChangesAsync(token);
            }

            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Resources"] = resourceDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.RemoveAll<IPackageLibraryTransport>();
                    services.AddSingleton<IPackageLibraryTransport>(new CachedPackage(manifest, archive));
                    services.RemoveAll<IOutboxHandler>();
                    services.AddScoped<IOutboxHandler, TestOutboxHandler>();
                });
            });
            resources.UseKestrel(0);
            using var resourceClient = resources.CreateClient();
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString,
                    ["Services:Resources:Url"] = resourceClient.BaseAddress!.ToString()
                }));
            });
            host.UseKestrel(0);
            using var hostClient = host.CreateClient();
            resources.Services.GetRequiredService<IConfiguration>()["Services:Host:Url"] = hostClient.BaseAddress!.ToString();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:Gateway:AgentIdentityAddress"] = hostClient.BaseAddress!.ToString(),
                    ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = hostClient.BaseAddress!.ToString()
                })));
            using var gatewayClient = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            using var assigned = await resourceClient.PutAsJsonAsync($"/internal/v1/agents/{agentId:D}/assignment", new AgentVersionAssignmentCommand(package.Id, "", Guid.CreateVersion7()), token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await assigned.Content.ReadFromJsonAsync<VersionAssignmentResult>(cancellationToken: token));
            var target = await resourceClient.GetFromJsonAsync<AgentVersionTarget>($"/internal/v1/agents/{agentId:D}/target", token);
            Assert.IsNotNull(target);
            hostClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentCredentials.Encode("vla", agentId, secret));
            gatewayClient.DefaultRequestHeaders.Authorization = hostClient.DefaultRequestHeaders.Authorization;
            var identity = await hostClient.GetFromJsonAsync<JsonElement>("/api/v1/agent/identity", token);
            Assert.AreEqual(agentId, identity.GetProperty("agentId").GetGuid());
            Assert.AreEqual(device.Id, identity.GetProperty("deviceId").GetGuid());
            var gatewayIdentity = await gatewayClient.GetFromJsonAsync<JsonElement>("/api/v1/agent/identity", token);
            Assert.AreEqual(agentId, gatewayIdentity.GetProperty("agentId").GetGuid());
            Assert.AreEqual(device.Id, gatewayIdentity.GetProperty("deviceId").GetGuid());
            var hostTarget = await hostClient.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(target, hostTarget);
            CollectionAssert.AreEqual(manifest, await hostClient.GetByteArrayAsync($"/api/v1/agent/update/targets/{manifestHash}.json", token));
            CollectionAssert.AreEqual(archive, await hostClient.GetByteArrayAsync($"/api/v1/agent/update/packages/{archiveHash}", token));
            var gatewayTarget = await gatewayClient.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(target, gatewayTarget);
            CollectionAssert.AreEqual(manifest, await gatewayClient.GetByteArrayAsync($"/api/v1/agent/update/targets/{manifestHash}.json", token));
            CollectionAssert.AreEqual(archive, await gatewayClient.GetByteArrayAsync($"/api/v1/agent/update/packages/{archiveHash}", token));
            using (var wrongPackage = await hostClient.GetAsync("/api/v1/agent/update/packages/missing", token))
                Assert.AreEqual(HttpStatusCode.NotFound, wrongPackage.StatusCode);
            var result = new AgentUpdateResult(Guid.CreateVersion7(), target.UpdateId, target.PackageId, target.ManifestSha256, 1, AgentUpdateResultKind.Succeeded, now, false, null);
            using var received = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", result, token);
            Assert.AreEqual(HttpStatusCode.NoContent, received.StatusCode);
            var status = await resourceClient.GetFromJsonAsync<AgentVersionAssignmentView>($"/internal/v1/agents/{agentId:D}/assignment", token);
            Assert.AreEqual(AgentUpdateResultKind.Succeeded, status!.LatestResult!.Result);
            Assert.AreEqual(package.Version, status.LatestResult.PackageVersion);
            using var revoked = await resourceClient.PostAsJsonAsync($"/internal/v1/agents/{agentId:D}/revoke", new AgentRevokeCommand(Guid.CreateVersion7()), token);
            Assert.AreEqual(AgentRegistrationStatus.Success, await revoked.Content.ReadFromJsonAsync<AgentRegistrationStatus>(cancellationToken: token));
            using var rejected = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", result, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using var revokedAssignment = await hostClient.GetAsync("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedAssignment.StatusCode);
            using var revokedPackage = await hostClient.GetAsync($"/api/v1/agent/update/packages/{archiveHash}", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedPackage.StatusCode);
            using var gatewayRevoked = await gatewayClient.GetAsync("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, gatewayRevoked.StatusCode);
        }, outerToken), context.CancellationToken);

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, Microsoft.Data.SqlClient.SqlConnection connection, Microsoft.Data.SqlClient.SqlTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CachedPackage(byte[] manifest, byte[] archive) : IPackageLibraryTransport
    {
        public bool IsCached(string archiveHash, string manifestHash) => true;
        public Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token) => Task.FromResult<byte[]?>(manifest);
        public Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token) => Task.FromResult<Stream?>(new MemoryStream(archive, writable: false));
        public Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] data, GitHubAgentReleases github, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> others, CancellationToken token) => throw new NotSupportedException();
    }
}
