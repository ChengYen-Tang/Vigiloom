using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class CpuReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Parse_HandlesAggregateAndCoresWithoutCountingGuestTwice()
    {
        var result = CpuReader.Parse(["cpu 100 20 30 500 10 5 5 10 40 10", "cpu0 50 10 15 250 5 2 3 5 20 5"]);
        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.AreEqual(100UL, result["cpu"].User);
        Assert.AreEqual(10UL, result["cpu"].Steal);
    }

    [TestMethod]
    public void PercentSince_RejectsResetZeroAndStaleIntervals()
    {
        var before = new CpuTicks(10, 0, 10, 80, 10, 0, 0, 0);
        var after = new CpuTicks(20, 0, 20, 100, 10, 0, 0, 0);
        var interval = TimeSpan.FromSeconds(1);
        Assert.AreEqual(50d, after.PercentSince(before, interval, interval));
        Assert.IsNull(before.PercentSince(after, interval, interval));
        Assert.IsNull(before.PercentSince(before, interval, interval));
        Assert.IsNull(after.PercentSince(before, TimeSpan.FromSeconds(2), interval));
        Assert.IsNull((after with { IoWait = 9 }).PercentSince(before, interval, interval));
    }

    [TestMethod]
    public void Parse_RejectsIncompleteDuplicateAndMissingAggregate()
    {
        Assert.IsNull(CpuReader.Parse(["cpu 1 2 3"]));
        Assert.IsNull(CpuReader.Parse(["cpu0 1 2 3 4 5 6 7 8"]));
        Assert.IsNull(CpuReader.Parse(["cpu 1 2 3 4 5 6 7 8", "cpu 1 2 3 4 5 6 7 8"]));
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReadsKernelCpuCounters()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires a Linux kernel."); return; }
        var values = await CpuReader.ReadAsync(TestContext.CancellationToken);
        Assert.IsNotNull(values);
        Assert.IsTrue(values.ContainsKey("cpu"));
        Assert.IsGreaterThan(1, values.Count);
    }
}
