using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.NetworkMonitoring.Application;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class RemoteDevicePingSourceTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task ReadsAndVerifiesTargetsFromResourceService()
    {
        var id = Guid.CreateVersion7();
        var version = new byte[] { 1, 2, 3 };
        var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/internal/v1/ping-targets")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new PingDeviceSnapshot(id, version, ["example.org"]) }) };
            if (request.Method == HttpMethod.Post)
            {
                var target = await request.Content!.ReadFromJsonAsync<PingTargetVerification>(cancellationToken: TestContext.CancellationToken);
                Assert.IsNotNull(target);
                Assert.AreEqual(id, target.DeviceId);
                CollectionAssert.AreEqual(version, target.Version);
                return new HttpResponseMessage(target.Target == "example.org" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PingDeviceContext("Example", [])) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://resources/") };
        var source = new RemoteDevicePingSource(client);
        var found = new List<PingDeviceSnapshot>();
        await foreach (var item in source.ReadEnabledAsync(TestContext.CancellationToken)) found.Add(item);
        Assert.AreEqual(id, Assert.ContainsSingle(found).Id);
        Assert.IsTrue(await source.IsCurrentAsync(id, version, "example.org", null!, null!, TestContext.CancellationToken));
        Assert.IsFalse(await source.IsCurrentAsync(id, version, "old.example.org", null!, null!, TestContext.CancellationToken));
        Assert.AreEqual("Example", (await source.GetContextAsync(id, null!, null!, TestContext.CancellationToken)).Name);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
