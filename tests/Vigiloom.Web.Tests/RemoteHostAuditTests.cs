using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Diagnostics;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteHostAuditTests(TestContext context)
{
    [TestMethod]
    public async Task AuditReaderRoutesHostSourcesToHostService()
    {
        var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var expected = new AuditFeedPage(AuditFeedSource.AgentMetricSettings, [], 1, false, false);
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual("/internal/v1/audit/query", request.RequestUri!.AbsolutePath);
            var input = await request.Content!.ReadFromJsonAsync<AuditFeedRequest>(context.CancellationToken);
            Assert.AreEqual(expected.Source, input!.Source);
            Assert.AreEqual(query, input.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        })) { BaseAddress = new Uri("http://host/") };
        using var services = new ServiceCollection().AddSingleton(new RemoteHostAuditFeed(client)).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:HostMonitoring:Url"] = "http://host" }).Build();
        var reader = new AuditFeedReader(null!, null!, null!, null!, null!, config, services);
        var actual = await reader.QueryAsync(new ClaimsPrincipal(), expected.Source, query, context.CancellationToken);
        Assert.AreEqual(expected.Source, actual.Source);
        Assert.AreEqual(expected.Page, actual.Page);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
