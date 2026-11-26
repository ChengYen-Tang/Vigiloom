using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteNetworkPublicTests(TestContext context)
{
    [TestMethod]
    public void NetworkConfigurationRequiresResourceService()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Services:Network:Url"] = "http://network", ["Services:Resources:Url"] = "" })));
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());
        Assert.AreEqual("Services:Resources:Url is required when Services:Network:Url is configured.", error.Message);
    }

    [TestMethod]
    public async Task UsesNetworkStatusAndHidesWithdrawnPublication()
    {
        var id = Guid.CreateVersion7();
        var visible = true;
        var revokeDuringRead = false;
        var policy = new PublishedDevicePolicy(id, "Public device", "private.example", true, true, true,
            DateTimeOffset.UtcNow.AddDays(-1), 1, DateTimeOffset.UtcNow.AddDays(-1), "version", "template");
        using var resource = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PublicPage<PublishedDevicePolicy>(visible ? [policy] : [], 1, false)) };
            return visible ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(policy) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("http://resources/") };
        using var network = new HttpClient(new Handler(request =>
        {
            if (revokeDuringRead) visible = false;
            if (request.RequestUri!.AbsolutePath.EndsWith("/internal/v1/public-ping", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new PublicDevice(id, "Public device", "Operational", 2, null) }) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/history", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PublicDeviceHistory([])) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PublicDevice(id, "Public device", "Operational", 2, null)) };
        })) { BaseAddress = new Uri("http://network/") };
        using var provider = new ServiceCollection().AddSingleton(new RemoteNetworkPublicClient(network)).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Network:Url"] = "http://network" }).Build();
        var reader = new RemotePublicReader(resource, null!, null!, null!, config, provider);
        var listed = await reader.DevicesAsync(null, 1, null, context.CancellationToken);
        Assert.AreEqual("Operational", Assert.ContainsSingle(listed.Items).State);
        Assert.AreEqual("Operational", (await reader.DeviceAsync(id, context.CancellationToken))!.State);
        var history = new PublicDeviceHistoryReader(null!, null!, config, provider);
        Assert.IsNotNull(await history.ReadAsync(id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, context.CancellationToken));
        revokeDuringRead = true;
        Assert.IsEmpty((await reader.DevicesAsync(null, 1, null, context.CancellationToken)).Items);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
