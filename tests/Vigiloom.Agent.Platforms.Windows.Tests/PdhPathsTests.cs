using Vigiloom.Agent.Platforms.Windows.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class PdhPathsTests
{
    [TestMethod]
    public void Expand_ResolvesDiskInstancesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var paths = PdhPaths.Expand(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        Assert.IsNotEmpty(paths);
        Assert.IsTrue(paths.All(path => path.StartsWith('\\') && path.Contains('(') && path.Contains(')')));
    }
}
