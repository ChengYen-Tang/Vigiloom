using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class DiskReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseAndCalculate_UsesKernelSectorAndCompletedOperationUnits()
    {
        var first = DiskReader.Parse(["8 0 sda 10 0 100 50 20 0 200 60 0 0 0"]);
        var second = DiskReader.Parse(["8 0 sda 14 0 120 62 22 0 240 70 0 0 0"]);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        var rate = second.Devices["8:0:sda"].Since(first.Devices["8:0:sda"], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        Assert.IsNotNull(rate);
        Assert.AreEqual(5120d, rate.Value.ReadBytesPerSecond);
        Assert.AreEqual(10240d, rate.Value.WriteBytesPerSecond);
        Assert.AreEqual(3d, rate.Value.ReadLatencyMilliseconds);
        Assert.AreEqual(5d, rate.Value.WriteLatencyMilliseconds);
    }

    [TestMethod]
    public void Calculate_ResetOrStaleReturnsMissingAndIdleHasNoLatency()
    {
        var initial = new DiskCounters(10, 100, 10, 10, 100, 10);
        Assert.IsNull(initial.Since(new(11, 100, 10, 10, 100, 10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
        Assert.IsNull(initial.Since(initial, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5)));
        var idle = initial.Since(initial, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.IsNotNull(idle);
        Assert.AreEqual(0d, idle.Value.ReadBytesPerSecond);
        Assert.IsNull(idle.Value.ReadLatencyMilliseconds);
    }

    [TestMethod]
    public void Parse_RejectsMalformedOrDuplicateDevicesAndBoundsOutput()
    {
        const string valid = "8 0 sda 10 0 100 50 20 0 200 60 0 0 0";
        Assert.IsNull(DiskReader.Parse([valid, valid]));
        Assert.IsNull(DiskReader.Parse(["8 0 sda invalid"]));
        var many = Enumerable.Range(0, DiskReader.MaximumDevices + 1).Select(i => $"8 {i} d{i} 1 0 1 1 1 0 1 1 0 0 0");
        var result = DiskReader.Parse(many);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Truncated);
        Assert.HasCount(DiskReader.MaximumDevices, result.Devices);
    }

    [TestMethod]
    public async Task ReadAsync_ReadsRealKernelCountersOnLinux()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var snapshot = await DiskReader.ReadAsync(TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.IsNotEmpty(snapshot.Devices);
    }
}
