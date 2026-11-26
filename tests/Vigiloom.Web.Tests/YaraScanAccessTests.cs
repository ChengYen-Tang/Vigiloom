using System.Net;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Web.AgentVersions;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class YaraScanAccessTests
{
    [TestMethod]
    public async Task RemoteBadRequestPreservesInputErrorForAllOperations()
    {
        using var http = new HttpClient(new BadRequestHandler()) { BaseAddress = new Uri("http://security/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Security:Url"] = "http://security" }).Build();
        var scans = new YaraScanAccess(null!, new Owners(), new Clients(http), config);
        var device = Guid.CreateVersion7();
        var scan = Guid.CreateVersion7();
        await Assert.ThrowsAsync<ArgumentException>(() => scans.ListAsync(device, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => scans.GetAsync(device, scan, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => scans.CancelAsync(device, scan, CancellationToken.None));
    }

    private sealed class Owners : IAgentOwnerResolver
    {
        public Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token) => Task.FromResult<InventoryOwner?>(null);
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class BadRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
}
