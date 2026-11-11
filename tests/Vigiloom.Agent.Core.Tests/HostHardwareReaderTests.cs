using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class HostHardwareReaderTests
{
    [TestMethod]
    public void ReadNetwork_ProducesBoundedEntriesWithValidParentReferences()
    {
        var read = HostHardwareReader.ReadNetwork(CancellationToken.None);
        Assert.IsTrue(read.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        var interfaces = read.Items.Where(x => x.ResourceType == "interface").Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var address in read.Items.Where(x => x.ResourceType == "address"))
        {
            Assert.IsNotNull(address.ParentKey);
            Assert.Contains(address.ParentKey, interfaces);
            Assert.IsTrue(System.Net.IPAddress.TryParse(address.Address, out _));
        }
        AssertTransportable(InventorySource.HostNetwork, read);
    }

    [TestMethod]
    public void ReadVolumes_ProducesBoundedTransportableEntries()
    {
        var read = HostHardwareReader.FromVolumes(new VolumeSample([new("/data", 4096, 1024)], 0, false));
        Assert.AreEqual(InventoryReadStatus.Complete, read.Status);
        Assert.IsTrue(read.Items.All(x => x.ResourceType == "volume"));
        AssertTransportable(InventorySource.HostVolumes, read);
        Assert.AreEqual(InventoryReadStatus.Partial, HostHardwareReader.FromVolumes(new VolumeSample([], 1, false)).Status);
    }

    private static void AssertTransportable(InventorySource source, InventoryReadResult<InventoryEntry> read)
    {
        var now = DateTimeOffset.UtcNow;
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, source, now, now,
            (InventoryOutcome)read.Status, read.Items);
        foreach (var batch in batches) Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out _));
    }
}
