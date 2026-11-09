using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class VolumeReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseMounts_OnlySelectsKnownLocalTypesAndDecodesMountNames()
    {
        string[] lines = [
            "20 1 8:1 / / rw - ext4 /dev/sda1 rw",
            "21 1 0:2 / /mnt/my\\040data rw - xfs /dev/sdb1 rw",
            "22 1 0:3 / /remote rw - nfs server:/share rw",
            "23 1 0:4 / /ondemand rw - autofs systemd rw",
            "24 1 0:5 / /fuse rw - fuse.sshfs user@host rw",
            "25 1 0:6 / /bad\\012path rw - ext4 /dev/sdc rw"
        ];
        var result = VolumeReader.ParseMounts(lines);
        CollectionAssert.AreEqual(new[] { "/", "/mnt/my data" }, result.Mounts.ToArray());
        Assert.AreEqual(1, result.InvalidMounts);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void ParseMounts_CapsCandidatePaths()
    {
        var lines = Enumerable.Range(0, 300).Select(i => $"{i + 1} 1 8:1 / /disk{i} rw - ext4 /dev/sda rw");
        var result = VolumeReader.ParseMounts(lines);
        Assert.HasCount(256, result.Mounts);
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public void ParseMounts_DoesNotProbeLocalMountHiddenByUnknownOrRemoteMount()
    {
        string[] lines = [
            "20 1 8:1 / / rw - ext4 /dev/sda1 rw",
            "21 1 8:2 / /mnt/data rw - ext4 /dev/sdb1 rw",
            "22 1 0:3 / /mnt/data rw - nfs server:/share rw",
            "23 1 8:3 / /remote/child rw - xfs /dev/sdc rw",
            "24 1 0:4 / /remote rw - autofs systemd rw",
            "25 1 8:4 / /mnt/other rw - ext4 /dev/sdd rw"
        ];
        var result = VolumeReader.ParseMounts(lines);
        CollectionAssert.AreEqual(new[] { "/", "/mnt/other" }, result.Mounts.ToArray());
        Assert.AreEqual(2, result.InvalidMounts);
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReadsKnownMountedVolume()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await VolumeReader.ReadAsync(TestContext.CancellationToken);
        Assert.IsNotEmpty(result.Volumes, "The test environment must expose a known local mount.");
        Assert.IsTrue(result.Volumes.All(x => x.TotalBytes > 0 && x.AvailableBytes >= 0 && x.AvailableBytes <= x.TotalBytes));
    }
}
