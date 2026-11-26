using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Web.Network;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemotePingStatusTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsStatusFromNetworkService()
    {
        var id = Guid.CreateVersion7();
        var status = new DevicePingStatus(id, [new PingTargetStatus("example.org", null, "central", Reachability.Unknown, "Pending", null, null, null, false)]);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal)
                    ? JsonContent.Create(new Dictionary<Guid, DevicePingStatus> { [id] = status }) : JsonContent.Create(status)
            };
        })) { BaseAddress = new Uri("http://network/") };
        var reader = new RemotePingStatusReader(client);
        var single = await reader.GetStatusAsync(id, ["example.org"], "version", context.CancellationToken);
        var list = await reader.GetStatusesAsync([new PingDeviceSnapshot(id, new byte[8], ["example.org"])], context.CancellationToken);
        Assert.AreEqual("example.org", Assert.ContainsSingle(single.Targets).Target);
        Assert.AreEqual(id, list[id].DeviceId);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
