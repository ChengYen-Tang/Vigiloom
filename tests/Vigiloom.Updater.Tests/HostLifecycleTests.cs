using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Updater.Hosting;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Host_WithoutProductConfiguration_CanStartAndStop()
    {
        var builder = UpdaterHost.CreateBuilder([]);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(testContext.CancellationToken);
        Assert.IsFalse(lifetime.ApplicationStopped.IsCancellationRequested);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsTrue(lifetime.ApplicationStopped.IsCancellationRequested);
    }
}
