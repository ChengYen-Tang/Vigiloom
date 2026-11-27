using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        using var services = new ServiceCollection().BuildServiceProvider();
        var scans = new YaraScanAccess(services, new Clients(http), config);
        var device = Guid.CreateVersion7();
        var scan = Guid.CreateVersion7();
        await Assert.ThrowsAsync<ArgumentException>(() => scans.ListAsync(device, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => scans.GetAsync(device, scan, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => scans.CancelAsync(device, scan, CancellationToken.None));
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
