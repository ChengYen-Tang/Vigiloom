using System.Runtime.Versioning;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class BootTimeReaderTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Read_ObtainsWindowsBootTimeWhenAvailable()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var boot = BootTimeReader.Read(CancellationToken.None);
        Assert.IsNotNull(boot);
        Assert.IsLessThanOrEqualTo(DateTimeOffset.UtcNow, boot.Value);
        Assert.AreEqual(TimeSpan.Zero, boot.Value.Offset);
    }
}
