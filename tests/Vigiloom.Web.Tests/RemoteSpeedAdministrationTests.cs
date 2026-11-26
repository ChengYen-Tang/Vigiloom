using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Web.SpeedTests;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteSpeedAdministrationTests(TestContext context)
{
    [TestMethod]
    public async Task SendsSettingsAndPreservesConflictAndCancellationResults()
    {
        var actor = Guid.CreateVersion7();
        var input = new SpeedSettingsInput(false, 6, 2, 40, "version");
        using var client = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Assert.AreEqual("/internal/v1/speed/settings", request.RequestUri.AbsolutePath);
            var command = await request.Content!.ReadFromJsonAsync<SpeedSettingsCommand>(context.CancellationToken);
            Assert.IsNotNull(command);
            Assert.AreEqual(actor, command.Actor);
            Assert.AreEqual(input, command.Input);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        })) { BaseAddress = new Uri("http://network/") };
        var remote = new RemoteSpeedAdministration(client);
        Assert.IsNull(await ((ISpeedSettings)remote).UpdateAsync(input, actor, context.CancellationToken));
        Assert.IsFalse(await ((ISpeedCancellation)remote).CancelAsync(Guid.CreateVersion7(), context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
