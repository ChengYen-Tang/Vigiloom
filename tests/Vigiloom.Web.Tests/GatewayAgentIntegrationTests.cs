extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class GatewayAgentIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task AgentEnrollmentAndRevocationWorkThroughGateway() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var web = PingTestSite.CreateFactory(database.ConnectionString);
            web.UseKestrel(0);
            await PingTestSite.InitializeAsync(web, token);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            var device = new Device("gateway agent", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                db.Devices.Add(device);
                await db.SaveChangesAsync(token);
            }
            await using var scope = web.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
            var actor = Guid.CreateVersion7();
            var issued = await identity.IssueAsync(device.Id, actor, token);
            var secret = RandomNumberGenerator.GetBytes(32);
            var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(host =>
                host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReverseProxy:Clusters:agent:Destinations:agent:Address"] = address,
                    ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = address,
                    ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = address,
                    ["Vigiloom:Gateway:AgentIdentityAddress"] = address
                })));
            using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var enrollment = await client.PostAsJsonAsync("/api/v1/agent/enroll", new EnrollmentRequest(issued.Secret!, SHA256.HashData(secret)), token);
            Assert.AreEqual(HttpStatusCode.OK, enrollment.StatusCode);
            var registered = await enrollment.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: token);
            Assert.IsNotNull(registered);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{registered.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}");
            using (var accepted = await client.GetAsync("/api/v1/agent/identity", token))
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(registered.AgentId, actor, token));
            using var revoked = await client.GetAsync("/api/v1/agent/identity", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, context.CancellationToken);
}
