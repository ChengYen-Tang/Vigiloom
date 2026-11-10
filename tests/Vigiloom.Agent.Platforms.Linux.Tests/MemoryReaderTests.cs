using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class MemoryReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadAsync_OnLinuxReadsKernelMemory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires a Linux kernel."); return; }
        var result = await MemoryReader.ReadAsync(TestContext.CancellationToken);
        Assert.IsNotNull(result);
        Assert.IsGreaterThan(0UL, result.Value.TotalBytes);
        Assert.IsLessThanOrEqualTo(result.Value.TotalBytes, result.Value.AvailableBytes);
    }

    [TestMethod]
    public void Parse_UsesAvailableMemoryAndPreservesZeroSwap()
    {
        const string input = "MemTotal:  1000 kB\nMemFree: 100 kB\nMemAvailable: 600 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n";
        var result = MemoryReader.Parse(input);
        Assert.IsNotNull(result);
        Assert.AreEqual(1_024_000UL, result.Value.TotalBytes);
        Assert.AreEqual(614_400UL, result.Value.AvailableBytes);
        Assert.AreEqual(0UL, result.Value.SwapTotalBytes);
        Assert.AreEqual(0UL, result.Value.SwapFreeBytes);
    }

    [TestMethod]
    public void Parse_DoesNotInventUnavailableOrInvalidValues()
    {
        Assert.IsNull(MemoryReader.Parse("MemTotal: 100 kB\nMemFree: 50 kB\n"));
        Assert.IsNull(MemoryReader.Parse("MemTotal: 100 kB\nMemAvailable: 101 kB\n"));
        Assert.IsNull(MemoryReader.Parse("MemTotal: 100 kB\nMemAvailable: 50 kB\nSwapTotal: 1 kB\n"));
        Assert.IsNull(MemoryReader.Parse("MemTotal: 100 kB\nMemTotal: 100 kB\nMemAvailable: 50 kB\n"));
        Assert.IsNull(MemoryReader.Parse("MemTotal: 18014398509481984 kB\nMemAvailable: 1 kB\n"));
    }
}
