using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Web.Services;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteReceptionTests(TestContext context)
{
    [TestMethod]
    public async Task OverviewAndRejectionsUseObservationService()
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler(id, now)) { BaseAddress = new Uri("http://observability/") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Observability:Url"] = "http://observability" }).Build();
        var client = new ReceptionManagement(http, configuration, null!, null!);
        var sources = await client.ListAsync(2, context.CancellationToken);
        Assert.AreEqual(id, sources.Single().SourceId);
        Assert.AreEqual(now, sources[0].LastLogAt);
        var rejected = await client.RejectionsAsync(context.CancellationToken);
        Assert.AreEqual(7L, rejected.Single().Count);
    }

    private sealed class Handler(Guid id, DateTimeOffset now) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object body = request.RequestUri!.PathAndQuery switch
            {
                "/internal/v1/reception?page=2" => new[] { new SourceReceptionSummary(id, "Source", null, now, null, null) },
                "/internal/v1/reception/rejections" => new[] { new ReceptionRejectionCount(1, 7, now, now, null, null, null) },
                _ => throw new InvalidOperationException("Unexpected reception path.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
