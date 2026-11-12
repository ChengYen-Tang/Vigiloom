using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class InventoryScanSchedulerTests
{
    [TestMethod]
    public void Next_AlternatesPassesAndUsesIndependentIntervals()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new InventoryScanScheduler(clock, [InventorySource.HostSystem], [InventorySource.HostProcesses, InventorySource.HostListeners]);
        Assert.AreEqual(InventorySource.HostSystem, scheduler.Next(3600, 60));
        // Finishing an unrelated outstanding source must not skip the selected item.
        scheduler.FinishSource(InventorySource.LinuxDpkg);
        Assert.AreEqual(InventorySource.HostSystem, scheduler.Next(3600, 60));
        scheduler.FinishSource(InventorySource.HostSystem);
        Assert.AreEqual(InventorySource.HostProcesses, scheduler.Next(3600, 60));
        scheduler.FinishSource(InventorySource.HostProcesses);
        Assert.AreEqual(InventorySource.HostListeners, scheduler.Next(3600, 60));
        scheduler.FinishSource(InventorySource.HostListeners);
        Assert.IsNull(scheduler.Next(3600, 60));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(InventorySource.HostProcesses, scheduler.Next(3600, 60));
        scheduler.Reset();
        Assert.AreEqual(InventorySource.HostSystem, scheduler.Next(3600, 60));
    }
}
