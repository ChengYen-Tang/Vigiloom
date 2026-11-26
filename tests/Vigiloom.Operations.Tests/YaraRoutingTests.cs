extern alias HostMonitoring;
extern alias SecurityHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class YaraRoutingTests(TestContext context)
{
    [TestMethod]
    public Task HostAuthenticatesAgentBeforeReadingSecurityWork() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new SecurityAnalysisDbContext(options)) await db.Database.MigrateAsync(token);
            using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Security"] = database.ConnectionString }));
            });
            security.UseKestrel(0);
            using var securityClient = security.CreateClient();
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            using var created = await securityClient.PostAsJsonAsync("/internal/v1/yara-scans/create",
                new YaraCreateCommand(identity, new("/tmp/test", "rule test { condition: true }", 1024, 30), Guid.CreateVersion7()), token);
            created.EnsureSuccessStatusCode();
            var job = await created.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token);
            Assert.IsNotNull(job);
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.UseSetting("Hangfire:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Services:Security:Url"] = securityClient.BaseAddress!.ToString() }));
                web.ConfigureTestServices(services => services.AddHttpClient("resources")
                    .ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity)));
            });
            using var agent = host.CreateClient();
            using (var noCredential = await agent.GetAsync("/api/v1/agent/yara-scans/next", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, noCredential.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            using (var inventory = await agent.GetAsync("/api/v1/agent/inventory-settings?contract=2", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, inventory.StatusCode);
                var snapshot = await inventory.Content.ReadFromJsonAsync<InventorySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(snapshot);
                using var cached = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/inventory-settings?contract=2");
                cached.Headers.IfNoneMatch.Add(inventory.Headers.ETag!);
                using var unchanged = await agent.SendAsync(cached, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            using (var unsupported = await agent.GetAsync("/api/v1/agent/inventory-settings?contract=3", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);
            using (var integrity = await agent.GetAsync("/api/v1/agent/integrity-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, integrity.StatusCode);
                Assert.IsNotNull(await integrity.Content.ReadFromJsonAsync<IntegritySettingsSnapshot>(cancellationToken: token));
                Assert.IsNotNull(integrity.Headers.ETag);
            }
            using (var baseline = await agent.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, baseline.StatusCode);
                Assert.IsNotNull(await baseline.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token));
                Assert.IsTrue(baseline.Headers.CacheControl!.NoStore);
            }
            Assert.AreEqual(job.Id, (await agent.GetFromJsonAsync<YaraScanJob>("/api/v1/agent/yara-scans/next", token))!.Id);
            using (var cancel = await securityClient.PostAsync($"/internal/v1/yara-scans/devices/{identity.DeviceId:D}/{job.Id:D}/cancel", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, cancel.StatusCode);
            using (var next = await agent.GetAsync("/api/v1/agent/yara-scans/next", token)) Assert.AreEqual(HttpStatusCode.NoContent, next.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "revoked");
            using var denied = await agent.GetAsync("/api/v1/agent/yara-scans/next", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var deniedSettings = await agent.GetAsync("/api/v1/agent/integrity-settings", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedSettings.StatusCode);
            using var deniedBaseline = await agent.GetAsync("/api/v1/agent/baseline-settings", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedBaseline.StatusCode);
        }, context.CancellationToken);

    private sealed class IdentityHandler(RegisteredAgentIdentity identity) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var credential = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            return credential?.Credential == "valid" ? new(HttpStatusCode.OK) { Content = JsonContent.Create(identity) } : new(HttpStatusCode.Unauthorized);
        }
    }
}
