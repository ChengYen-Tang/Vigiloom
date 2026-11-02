using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Agent.Hosting;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    public void IdentityHttpTransport_DoesNotRedirectOrShareCookies()
    {
        var builder = AgentHost.CreateBuilder([]);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var pooled = factory.CreateHandler("IdentityClient");
        HttpMessageHandler handler = pooled;
        while (handler is DelegatingHandler { InnerHandler: { } inner }) handler = inner;
        var sockets = Assert.IsInstanceOfType<SocketsHttpHandler>(handler);
        Assert.IsFalse(sockets.AllowAutoRedirect, "Enrollment secrets must not be replayed to redirect targets.");
        Assert.IsFalse(sockets.UseCookies);
        Assert.AreEqual(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Host_WithoutProductConfiguration_CanStartAndStop()
    {
        var builder = AgentHost.CreateBuilder([]);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(testContext.CancellationToken);
        Assert.IsFalse(lifetime.ApplicationStopped.IsCancellationRequested);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsTrue(lifetime.ApplicationStopped.IsCancellationRequested);
    }
}
