using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class InventoryManifestTests
{
    [TestMethod]
    public void Matches_RejectsChangedOwnerAndManifestWithoutDependingOnBatchIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 3);
        var measured = InventoryDigest.MeasureChunk([]);
        var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 2,
            InventorySource.LinuxGroups, now, InventoryOutcome.Complete, 0, 1, 0,
            measured.Bytes, InventoryDigest.Combine([measured.Digest]), []);
        var snapshot = new InventorySnapshot(owner, chunk, now);

        Assert.IsTrue(InventoryManifest.Matches(snapshot, owner, chunk with { BatchId = Guid.CreateVersion7() }));
        Assert.IsFalse(InventoryManifest.Matches(snapshot, owner with { DeviceGeneration = 4 }, chunk));
        Assert.IsFalse(InventoryManifest.Matches(snapshot, owner with { DeviceId = Guid.CreateVersion7() }, chunk));
        Assert.IsFalse(InventoryManifest.Matches(snapshot, owner, chunk with { SnapshotId = Guid.CreateVersion7() }));
        Assert.IsFalse(InventoryManifest.Matches(snapshot, owner, chunk with { Outcome = InventoryOutcome.Partial }));
        Assert.IsFalse(InventoryManifest.Matches(snapshot, owner, chunk with { Digest = new byte[32] }));
    }
}
