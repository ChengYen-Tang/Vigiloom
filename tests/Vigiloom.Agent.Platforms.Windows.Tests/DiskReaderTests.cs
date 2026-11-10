using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class DiskReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ToReading_OnlyReportsLatencyWhenThatDirectionHasOperations()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var idle = DiskReader.ToReading("disk", [0, 0, 0, 0, 0, 0]);
        Assert.IsNull(idle.ReadLatencyMilliseconds);
        Assert.IsNull(idle.WriteLatencyMilliseconds);
        var read = DiskReader.ToReading("disk", [500, 0, 2, 0, 0.025, 0]);
        Assert.AreEqual(25d, read.ReadLatencyMilliseconds);
        Assert.IsNull(read.WriteLatencyMilliseconds);
        var unknown = DiskReader.ToReading("disk", [0, 5, null, 1, 0.02, null]);
        Assert.IsNull(unknown.ReadLatencyMilliseconds);
        Assert.IsNull(unknown.WriteLatencyMilliseconds);
    }

    [TestMethod]
    public async Task Read_WarmsUpAndReturnsDiskRates()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var reader = new DiskReader(TimeProvider.System);
        var first = reader.Read(TimeSpan.FromMinutes(1));
        Assert.IsEmpty(first.Disks);
        Assert.IsTrue(first.Incomplete);
        await Task.Delay(500, TestContext.CancellationToken);
        var second = reader.Read(TimeSpan.FromMinutes(1));
        Assert.IsNotEmpty(second.Disks);
        Assert.IsTrue(second.Disks.Any(x => x.ReadBytesPerSecond is >= 0 && x.WriteBytesPerSecond is >= 0));
        Assert.IsTrue(second.Disks.All(x => x.ReadLatencyMilliseconds is null or >= 0 && x.WriteLatencyMilliseconds is null or >= 0));
        var stale = reader.Read(TimeSpan.FromTicks(1));
        Assert.IsEmpty(stale.Disks);
        Assert.IsTrue(stale.Incomplete);
    }
}
