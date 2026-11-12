using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Agent.Platforms.Windows.Processes;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class ParentProcessReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TryParseCreation_UsesExplicitOffsetAcrossDaylightSavingOverlap()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.IsTrue(ParentProcessReader.TryParseCreation("20261101013000.123456-240", out var first));
        Assert.IsTrue(ParentProcessReader.TryParseCreation("20261101013000.123456-300", out var second));
        Assert.AreEqual(TimeSpan.FromHours(1).Ticks / 10, second - first);
        Assert.IsFalse(ParentProcessReader.TryParseCreation("20261101013000.123456+***", out _));
    }

    [TestMethod]
    public void Read_MatchesCurrentProcessCreationTime()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = ParentProcessReader.Read(TestContext.CancellationToken);
        if (result.Status == InventoryReadStatus.PermissionDenied) { Assert.Inconclusive("WMI access denied."); return; }
        Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        var observed = ProcessReader.Read(TestContext.CancellationToken).Items.Single(x => x.ProcessId == Environment.ProcessId);
        var parent = result.Items.Single(x => x.ProcessId == Environment.ProcessId);
        Assert.AreEqual(observed.StartedAt.UtcTicks / 10, parent.CreationMicroseconds);
        Assert.IsGreaterThanOrEqualTo(0, parent.ParentProcessId);
    }
}
