using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class PageFileReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Read_OnWindowsReturnsBoundedPageFileUsage()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = PageFileReader.Read();
        TestContext.WriteLine($"Pagefile total: {result.TotalBytes} bytes; in use: {result.UsedBytes} bytes.");
        Assert.IsLessThanOrEqualTo(result.TotalBytes, result.UsedBytes);
        Assert.AreEqual(0UL, result.TotalBytes % (ulong)Environment.SystemPageSize);
        Assert.AreEqual(0UL, result.UsedBytes % (ulong)Environment.SystemPageSize);
    }
}
