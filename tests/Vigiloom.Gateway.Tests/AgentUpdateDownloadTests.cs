extern alias HostMonitoring;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Gateway.Tests;

[TestClass]
public sealed class AgentUpdateDownloadTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DownloadsUseAuthenticatedAgentAndRejectInvalidCredentials()
    {
        var agentId = Guid.CreateVersion7();
        using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("Temporal:Enabled", "false");
            web.UseSetting("Hangfire:Enabled", "false");
            web.ConfigureServices(services => services.AddHttpClient("resources")
                .ConfigurePrimaryHttpMessageHandler(() => new ResourceHandler(agentId)));
        });
        using var client = host.CreateClient();
        using (var denied = await client.GetAsync("/api/v1/agent/update/targets/manifest.json", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
        using (var manifest = await client.GetAsync("/api/v1/agent/update/targets/manifest.json", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, manifest.StatusCode);
            Assert.AreEqual("no-store", manifest.Headers.CacheControl?.ToString());
            Assert.AreEqual("{\"version\":1}", await manifest.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
        using (var package = await client.GetAsync("/api/v1/agent/update/packages/agent.zip", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, package.StatusCode);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await package.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));
        }
        using var missing = await client.GetAsync("/api/v1/agent/update/packages/missing.zip", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class ResourceHandler(Guid agentId) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/internal/v1/agents/authenticate")
            {
                var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
                return command?.Credential != "valid" ? new(HttpStatusCode.Unauthorized) : new(HttpStatusCode.OK)
                { Content = JsonContent.Create(new RegisteredAgentIdentity(agentId, Guid.CreateVersion7(), 1)) };
            }
            var prefix = $"/internal/v1/agents/{agentId:D}/update/";
            return request.RequestUri.AbsolutePath switch
            {
                var path when path == prefix + "targets/manifest.json" => new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") },
                var path when path == prefix + "packages/agent.zip" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) },
                _ => new(HttpStatusCode.NotFound)
            };
        }
    }
}
