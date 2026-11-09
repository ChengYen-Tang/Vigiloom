using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class ProcessIoReaderTests
{
    [TestMethod]
    public void Read_ReadsCurrentProcessOnWindows()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var first = ProcessIoReader.Read();
        var second = ProcessIoReader.Read();
        Assert.IsGreaterThanOrEqualTo(first.ReadBytes, second.ReadBytes);
        Assert.IsGreaterThanOrEqualTo(first.WriteBytes, second.WriteBytes);
    }
}
