using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Network;
using Vigiloom.Web.Network;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemotePingAdministrationTests(TestContext context)
{
    [TestMethod]
    public async Task SendsRetentionPreviewAndPreservesConflict()
    {
        var actor = Guid.CreateVersion7();
        var input = new PingRetentionPreviewRequest(10, 30, "version");
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual("/internal/v1/ping/retention/preview", request.RequestUri!.AbsolutePath);
            var command = await request.Content!.ReadFromJsonAsync<PingRetentionPreviewCommand>(context.CancellationToken);
            Assert.IsNotNull(command);
            Assert.AreEqual(actor, command.Actor);
            Assert.AreEqual(input, command.Input);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        })) { BaseAddress = new Uri("http://network/") };
        var remote = (IPingRetentionSettings)new RemotePingAdministration(client);
        Assert.IsNull(await remote.PreviewAsync(input, actor, context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
