using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Diagnostics;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteJobOverviewTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsOverviewFromOperations()
    {
        using var client = new HttpClient(new Handler()) { BaseAddress = new Uri("http://operations/") };
        var result = await new RemoteJobOverview(client).GetAsync(context.CancellationToken);
        Assert.AreEqual(2L, result.Workers);
        Assert.AreEqual(3, result.PendingMessages);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/internal/v1/diagnostics/jobs", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkOverview(2, 0, 0, 0, 0, 3, 0, [])) });
        }
    }
}
