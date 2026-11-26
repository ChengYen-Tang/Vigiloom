using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Web.Devices;
using Vigiloom.Web.HostMonitoring;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteHostLogsTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsOperationsLogsForExistingResource()
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var device = new DeviceDetails(id, "server", "server.local", [], true, true, false, now, now, "version");
        using var resources = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(device) })) { BaseAddress = new Uri("http://resources/") };
        using var operations = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual($"/internal/v1/host-logs/{id:D}", request.RequestUri!.AbsolutePath);
            Assert.Contains("cursor=a%2Bb%3D", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HostLogPage([], null)) };
        })) { BaseAddress = new Uri("http://operations/") };
        var history = new RemoteHostLogs(operations, new RemoteDeviceManagement(resources));
        var result = await history.GetAsync(id, now.AddHours(-1), now, "a+b=", null, context.CancellationToken);
        Assert.IsNotNull(result);
        Assert.IsEmpty(result.Items);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
