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
            Assert.AreEqual(job.Id, (await agent.GetFromJsonAsync<YaraScanJob>("/api/v1/agent/yara-scans/next", token))!.Id);
            using (var cancel = await securityClient.PostAsync($"/internal/v1/yara-scans/devices/{identity.DeviceId:D}/{job.Id:D}/cancel", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, cancel.StatusCode);
            using (var next = await agent.GetAsync("/api/v1/agent/yara-scans/next", token)) Assert.AreEqual(HttpStatusCode.NoContent, next.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "revoked");
            using var denied = await agent.GetAsync("/api/v1/agent/yara-scans/next", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
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
