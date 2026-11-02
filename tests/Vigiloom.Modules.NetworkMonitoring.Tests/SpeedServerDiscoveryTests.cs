using System.Net;
using System.Text;
using NetPace.Core.Clients.Ookla;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Testing.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedServerDiscoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string ValidConfig = """
        {"ispName":"TANet","ispId":1244,"clientAuth":{"token":"secret"},"servers":[
          {"url":"http://127.0.0.1:8080/speedtest/upload.php","lat":"25","lon":"121","name":"Bad","country":"Taiwan","sponsor":"Bad","id":"3"},
          {"url":"http://speedtest.example.com:8080/speedtest/upload.php","lat":"25.0","lon":"121.5","name":"Taipei","country":"Taiwan","sponsor":"Example","id":"14691","isp_id":"104044"}
        ]}
        """;

    [TestMethod]
    public async Task SuggestedServers_ParsesProviderDataAndRejectsPrivateEndpoint()
    {
        using var client = new HttpClient(new DiscoveryHandler(HttpStatusCode.OK, ValidConfig));
        var result = await SpeedServerDiscovery.GetSuggestedServersAsync(client, TestContext.CancellationToken);
        Assert.HasCount(1, result);
        var server = result[0] as OoklaServer;
        Assert.IsNotNull(server);
        Assert.AreEqual(14691, server.Id);
        Assert.AreEqual("Taipei", server.Location);
        Assert.AreEqual(121.5, server.Longitude);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "<html>challenge</html>")]
    [DataRow(HttpStatusCode.OK, "<html>not JSON</html>")]
    [DataRow(HttpStatusCode.OK, "[]")]
    [DataRow(HttpStatusCode.OK, "{\"servers\":[]}")]
    public async Task SuggestedServers_UnavailableOrInvalidReturnsEmptyForFallback(HttpStatusCode status, string body)
    {
        using var client = new HttpClient(new DiscoveryHandler(status, body));
        var result = await SpeedServerDiscovery.GetSuggestedServersAsync(client, TestContext.CancellationToken);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task SuggestedServers_PropagatesCancellation()
    {
        using var client = new HttpClient(new DiscoveryHandler(HttpStatusCode.OK, ValidConfig));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => SpeedServerDiscovery.GetSuggestedServersAsync(client, cancelled.Token));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasureAsync_UsesSuggestedServerWithoutQueryingStaticDirectory()
    {
        var handler = new DiscoveryHandler(HttpStatusCode.OK, ValidConfig, new ControlledPhaseHandler());
        var probe = new SpeedtestNetProbe(new DiscoveryFactory(handler), 2000, 4);
        var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), TestContext.CancellationToken);
        Assert.AreEqual("Example (#14691) · speedtest.example.com", result.Server);
        Assert.AreEqual(0, handler.StaticRequests);
        Assert.IsGreaterThan(0, handler.ConfigRequests);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasureAsync_RequestsAndUsesExactServerId()
    {
        var handler = new DiscoveryHandler(HttpStatusCode.OK, ValidConfig, new ControlledPhaseHandler());
        var probe = new SpeedtestNetProbe(new DiscoveryFactory(handler), 2000, 4);
        var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), 14691, TestContext.CancellationToken);
        Assert.AreEqual("Example (#14691) · speedtest.example.com", result.Server);
        Assert.AreEqual("?server_ids%5B%5D=14691", handler.ConfigQuery);
        Assert.AreEqual(0, handler.StaticRequests);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasureAsync_UnreachableSuggestionsFallBackToStaticDirectory()
    {
        var config = ValidConfig.Replace("speedtest.example.com", "unreachable.example.com", StringComparison.Ordinal);
        var handler = new DiscoveryHandler(HttpStatusCode.OK, config, new ControlledPhaseHandler());
        var probe = new SpeedtestNetProbe(new DiscoveryFactory(handler), 2000, 4);
        var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), TestContext.CancellationToken);
        Assert.AreEqual("Example (#1) · speedtest.example.com", result.Server);
        Assert.IsGreaterThan(0, handler.StaticRequests);
    }

    private sealed class DiscoveryFactory(DiscoveryHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class DiscoveryHandler(HttpStatusCode status, string body, HttpMessageHandler? inner = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker? invoker = inner is null ? null : new HttpMessageInvoker(inner);
        public int ConfigRequests { get; private set; }
        public int StaticRequests { get; private set; }
        public string? ConfigQuery { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/api/js/config-sdk")
            {
                ConfigRequests++;
                ConfigQuery = request.RequestUri.Query;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
            }
            if (request.RequestUri.Host == "unreachable.example.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
            if (request.RequestUri.AbsolutePath.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal)) StaticRequests++;
            return invoker!.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker?.Dispose();
            base.Dispose(disposing);
        }
    }
}
