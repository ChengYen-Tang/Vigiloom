using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class WindowsReleaseReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TryCreate_RequiresStructuredBuildAndRevision()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.IsTrue(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348", 3000, out var result));
        Assert.AreEqual(new WindowsRelease("ServerStandard", "21H2", 20348, 3000), result);
        Assert.IsTrue(WindowsReleaseReader.TryCreate("ServerStandard", null, "20348", 0, out _));
        Assert.IsFalse(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348.3000", 3000, out _));
        Assert.IsFalse(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348", null, out _));
        Assert.IsFalse(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348", -1, out _));
        Assert.IsFalse(WindowsReleaseReader.TryCreate("Server\nStandard", "21H2", "20348", 1, out _));
        Assert.IsTrue(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348", 3000, out var core, "Server Core"));
        Assert.AreEqual("Server Core", core!.InstallationType);
        Assert.IsFalse(WindowsReleaseReader.TryCreate("ServerStandard", "21H2", "20348", 3000, out _, "Server\nCore"));
    }

    [TestMethod]
    public void Read_ReportsObservedValuesOrIncompleteEvidence()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = WindowsReleaseReader.Read(TestContext.CancellationToken);
        Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial or InventoryReadStatus.PermissionDenied);
        if (result.Status == InventoryReadStatus.Complete)
        {
            Assert.HasCount(1, result.Items);
            Assert.IsGreaterThan(0, result.Items[0].Build);
            Assert.IsGreaterThanOrEqualTo(0, result.Items[0].Revision);
        }
        else Assert.IsEmpty(result.Items);
    }
}
