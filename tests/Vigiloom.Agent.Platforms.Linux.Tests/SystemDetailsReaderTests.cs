using Vigiloom.Agent.Platforms.Linux.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class SystemDetailsReaderTests
{
    [TestMethod]
    public void Parse_UsesReleaseFieldsWithoutExecutingShellContent()
    {
        var parsed = SystemDetailsReader.ParseRelease("ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"$(ignored)\"\n");
        Assert.AreEqual("ubuntu", parsed.Id);
        Assert.AreEqual("24.04", parsed.Version);
        Assert.IsFalse(parsed.Invalid);
        var malformedVersion = SystemDetailsReader.ParseRelease("ID=ubuntu\nVERSION_ID=\"unterminated\n");
        Assert.AreEqual("ubuntu", malformedVersion.Id);
        Assert.IsTrue(malformedVersion.Invalid);
        Assert.IsFalse(SystemDetailsReader.ParseRelease("ID=arch\n").Invalid);
        Assert.IsNull(SystemDetailsReader.ParseRelease("ID=\"unterminated\n").Id);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000), SystemDetailsReader.ParseBoot("cpu 1 2 3\nbtime 1700000000\n"));
        Assert.IsNull(SystemDetailsReader.ParseBoot("cpu 1 2 3\nbtime invalid\n"));
    }

    [TestMethod]
    public async Task Read_ReportsActualLocalBootAndDistributionOnLinux()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var details = await SystemDetailsReader.ReadAsync(CancellationToken.None);
        Assert.IsTrue(details.Complete);
        Assert.IsNotNull(details.DistributionId);
        Assert.IsNotNull(details.BootedAt);
        Assert.IsLessThanOrEqualTo(DateTimeOffset.UtcNow, details.BootedAt.Value);
    }
}
