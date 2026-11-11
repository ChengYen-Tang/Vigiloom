using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Linux.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class RpmReaderTests
{
    [TestMethod]
    public void Parse_PreservesEpochArchitectureAndMultipleVersions()
    {
        var (status, first) = RpmReader.Parse("kernel\t0:6.14.0-1.fc42\tx86_64\tFedora Project");
        var (_, second) = RpmReader.Parse("kernel\t0:6.15.0-1.fc42\tx86_64\tFedora Project");
        Assert.AreEqual(PackageCommand.LineStatus.Installed, status);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("0:6.14.0-1.fc42", first.Version);
        Assert.AreEqual("x86_64", first.Architecture);
        Assert.AreNotEqual(first.SourceKey, second.SourceKey);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, RpmReader.Parse("kernel\tmissing-fields").Status);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, RpmReader.Parse($"kernel\t{new string('x', 2048)}\tx86_64\tFedora").Status);
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReportsInstalledPackagesOrUnsupported()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await RpmReader.ReadAsync(CancellationToken.None);
        if (!File.Exists("/usr/bin/rpm")) Assert.AreEqual(InventoryReadStatus.Unsupported, result.Status);
        else
        {
            Assert.AreEqual(InventoryReadStatus.Complete, result.Status);
            Assert.IsNotEmpty(result.Items);
        }
    }
}
