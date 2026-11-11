using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class QuickFixReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TryCreate_PreservesSourceDateAndRejectsMalformedValues()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.IsTrue(QuickFixReader.TryCreate("KB123456", "Security Update", "11/12/2026", out var result));
        Assert.AreEqual(new InstalledPatch("KB123456", "Security Update", "11/12/2026"), result);
        Assert.IsFalse(QuickFixReader.TryCreate("KB\n123", null, null, out _));
        Assert.IsFalse(QuickFixReader.TryCreate("", null, null, out _));
    }

    [TestMethod]
    public void Read_QueriesWindowsQuickFixSource()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = QuickFixReader.Read(TestContext.CancellationToken);
        Assert.AreNotEqual(InventoryReadStatus.Error, result.Status);
        if (result.Status == InventoryReadStatus.PermissionDenied) Assert.Inconclusive("WMI access is denied.");
        Assert.IsTrue(result.Items.All(item => !string.IsNullOrWhiteSpace(item.SourceKey)));
    }

    [TestMethod]
    public void Read_ObservesCancellationBeforeStartingWmi()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (OperatingSystem.IsWindows()) QuickFixReader.Read(cancellation.Token);
        });
    }
}
