using Vigiloom.Agent.Platforms.Windows.Metrics;
using System.Runtime.InteropServices;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class CpuCoreReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Read_OnWindowsWarmsUpAndReturnsRealCoreValues()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var reader = new CpuCoreReader(TimeProvider.System);
        var initial = reader.Read(TimeSpan.FromMinutes(1));
        Assert.IsEmpty(initial.Cores);
        Assert.IsNull(initial.Total);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.CancellationToken);
        var sample = reader.Read(TimeSpan.FromMinutes(1));
        Assert.IsNotEmpty(sample.Cores);
        Assert.IsNotNull(sample.Total);
        Assert.IsFalse(sample.Incomplete);
        Assert.HasCount((int)GetActiveProcessorCount(0xffff), sample.Cores);
        Assert.IsTrue(sample.Cores.All(x => x.Value is >= 0 and <= 100));
        Assert.IsGreaterThanOrEqualTo(0d, sample.Total.Value);
        Assert.IsLessThanOrEqualTo(100d, sample.Total.Value);
        var stale = reader.Read(TimeSpan.FromTicks(1));
        Assert.IsEmpty(stale.Cores);
        Assert.IsNull(stale.Total);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
