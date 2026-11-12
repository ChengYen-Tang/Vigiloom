using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Processes;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class ServiceReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Read_EnumeratesServicesFromScm()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = ServiceReader.Read(TestContext.CancellationToken);
        if (result.Status == InventoryReadStatus.PermissionDenied) { Assert.Inconclusive("SCM access denied."); return; }
        Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        Assert.IsTrue(result.Items.Any(x => x.Key.Equals("EventLog", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(result.Items.Count, result.Items.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
