using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteLineTests(TestContext context)
{
    [TestMethod]
    public async Task PreservesWebhookAndAdministratorSettings()
    {
        var actor = Guid.CreateVersion7();
        using var client = new HttpClient(new Handler(actor)) { BaseAddress = new Uri("http://operations/") };
        var remote = new RemoteLine(client);
        var view = await remote.GetAsync(context.CancellationToken);
        Assert.IsTrue(view.Enabled);
        Assert.AreEqual(10, view.AggregationSeconds);
        Assert.IsTrue(await remote.SaveAsync(new(true, "token", "secret", 15), actor, context.CancellationToken));
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"events\":[]}"));
        request.ContentLength = request.Body.Length;
        request.Headers["x-line-signature"] = "signed-value";
        Assert.AreEqual(200, await remote.ReceiveAsync(request, context.CancellationToken));
    }

    [TestMethod]
    public async Task WebhookTimeoutReturnsUnavailable()
    {
        using var client = new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("http://operations/") };
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream();
        Assert.AreEqual(503, await new RemoteLine(client).ReceiveAsync(request, context.CancellationToken));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new OperationCanceledException();
    }

    private sealed class Handler(Guid actor) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new LineChannelInfo(true, true, true, [], 10)) };
            if (request.Method == HttpMethod.Put)
            {
                var input = await request.Content!.ReadFromJsonAsync<LineChannelChange>(token);
                Assert.IsNotNull(input);
                Assert.AreEqual(actor, input.ActorId);
                Assert.AreEqual(15, input.AggregationSeconds);
                return new(HttpStatusCode.NoContent);
            }
            Assert.AreEqual("/internal/v1/line/webhook", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("signed-value", request.Headers.GetValues("x-line-signature").Single());
            Assert.AreEqual("{\"events\":[]}", await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK);
        }
    }
}
