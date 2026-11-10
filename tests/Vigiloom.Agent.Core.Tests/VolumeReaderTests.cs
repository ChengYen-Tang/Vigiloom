using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class VolumeReaderTests
{
    [TestMethod]
    public void Read_ReturnsBoundedConsistentVolumeCapacities()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var sample = VolumeReader.ReadWindows();
        Assert.IsNotEmpty(sample.Volumes, "The test environment must expose at least one local mounted volume.");
        Assert.IsLessThanOrEqualTo(VolumeReader.MaximumVolumes, sample.Volumes.Count);
        Assert.IsTrue(sample.Volumes.All(volume => volume.TotalBytes > 0 && volume.AvailableBytes >= 0 && volume.AvailableBytes <= volume.TotalBytes));
        Assert.AreEqual(sample.Volumes.Count, sample.Volumes.Select(volume => volume.Mount).Distinct(StringComparer.Ordinal).Count());
    }
}
