using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Hosting;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentWorkNotificationsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NotificationsWakeTheirOwnWorkerAndPollingRemainsAvailable()
    {
        using var notifications = new AgentWorkNotifications();
        var clock = new FakeTimeProvider();
        var update = notifications.WaitForUpdateAsync(TimeSpan.FromMinutes(1), clock, TestContext.CancellationToken);
        var scan = notifications.WaitForScanAsync(TimeSpan.FromMinutes(1), clock, TestContext.CancellationToken);
        notifications.NotifyUpdate();
        await update.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        Assert.IsFalse(scan.IsCompleted);
        notifications.NotifyUpdate();
        notifications.NotifyUpdate();
        clock.Advance(TimeSpan.FromMinutes(1));
        await scan.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        await notifications.WaitForUpdateAsync(TimeSpan.FromMinutes(1), clock, TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
    }
}
