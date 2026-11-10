using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class MemoryReaderTests
{
    [TestMethod]
    public void Read_OnWindowsReturnsPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = MemoryReader.Read();
        Assert.IsGreaterThan(0UL, result.TotalBytes);
        Assert.IsLessThanOrEqualTo(result.TotalBytes, result.AvailableBytes);
    }
}
