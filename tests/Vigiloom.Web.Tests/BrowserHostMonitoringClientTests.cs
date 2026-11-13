using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class BrowserHostMonitoringClientTests
{
    [TestMethod]
    public async Task Metrics_OmitsAbsentCursorAndSendsKind()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HostMetricPage([], null)) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = Create(http);
        var id = Guid.CreateVersion7();
        await client.GetMetricsAsync(id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null, AgentMetricKind.CpuTotalPercent, TestContext.CancellationToken);
        Assert.IsNotNull(handler.LastUri);
        Assert.DoesNotContain("cursor=", handler.LastUri.Query);
        Assert.Contains("kind=1", handler.LastUri.Query);
    }

    [TestMethod]
    public async Task InventoryChunk_NotFoundIsDistinctFromUnauthorized()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = Create(http);
        var id = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        Assert.IsNull(await client.GetInventoryChunkAsync(id, Vigiloom.Contracts.Agent.Inventory.InventorySource.HostProcesses, snapshot, 0, TestContext.CancellationToken));
        handler.Response = new(HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetInventoryChunkAsync(id, Vigiloom.Contracts.Agent.Inventory.InventorySource.HostProcesses, snapshot, 0, TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; } = null!;
    private static IHostMonitoringClient Create(HttpClient http)
    {
        var type = typeof(TimeSeriesChart).Assembly.GetType("Vigiloom.Web.Client.Services.BrowserHostMonitoringClient", throwOnError: true)!;
        return (IHostMonitoringClient)Activator.CreateInstance(type, http)!;
    }
    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = response;
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { LastUri = request.RequestUri; return Task.FromResult(Response); }
    }
}
