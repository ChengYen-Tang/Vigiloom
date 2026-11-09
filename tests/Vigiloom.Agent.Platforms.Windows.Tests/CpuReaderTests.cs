using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class CpuReaderTests
{
    [TestMethod]
    public void PercentSince_AccountsForIdleWithinKernelAndRejectsInvalidSamples()
    {
        var first = new CpuTicks(100, 200, 100);
        var next = new CpuTicks(160, 280, 120);
        var second = TimeSpan.FromSeconds(1);
        Assert.AreEqual(40d, next.PercentSince(first, second, second));
        Assert.IsNull(first.PercentSince(next, second, second));
        Assert.IsNull(next.PercentSince(first, TimeSpan.Zero, second));
        Assert.IsNull(next.PercentSince(first, TimeSpan.FromSeconds(2), second));
        Assert.IsNull((next with { Idle = 190 }).PercentSince(first, second, second));
    }

    [TestMethod]
    public void Read_OnWindowsReturnsTimesForSingleProcessorGroup()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var sample = CpuReader.Read();
        if (sample is null) { Assert.Inconclusive("Multiple processor groups are not represented by GetSystemTimes."); return; }
        Assert.IsGreaterThan(0UL, sample.Value.Kernel);
        Assert.IsLessThanOrEqualTo(sample.Value.Kernel, sample.Value.Idle);
    }
}
