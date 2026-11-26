using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Web.SpeedTests;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteSpeedHistoryTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsHistoryAndHandlesMissingResult()
    {
        using var client = new HttpClient(new Handler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SpeedHistoryPage([], 0, 1, 50, [], false, null)) }
            : new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://network/") };
        var history = new RemoteSpeedHistory(client);
        var now = DateTimeOffset.UtcNow;
        var result = await history.QueryAsync(new SpeedHistoryQuery(now.AddHours(-1), now), context.CancellationToken);
        Assert.AreEqual(0, result.Total);
        Assert.IsNull(await history.GetAsync(Guid.CreateVersion7(), context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
