using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class LoadReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Parse_UsesInvariantDecimalsAndRejectsInvalidValues()
    {
        Assert.AreEqual(new LoadAverages(0.1, 1.25, 7.5), LoadReader.Parse("0.10 1.25 7.50 2/100 12\n"));
        Assert.IsNull(LoadReader.Parse("1.0 2.0"));
        Assert.IsNull(LoadReader.Parse("1.0 -2.0 3.0"));
        Assert.IsNull(LoadReader.Parse("1.0 NaN 3.0"));
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReadsKernelLoad()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await LoadReader.ReadAsync(TestContext.CancellationToken);
        Assert.IsNotNull(result);
        Assert.IsGreaterThanOrEqualTo(0d, result.Value.OneMinute);
    }
}
