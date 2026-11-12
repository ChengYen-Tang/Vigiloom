using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class SnapshotVerificationTests
{
    [TestMethod]
    public void Verify_AcceptsOutOfOrderAndRejectsMissingOrMutatedChunks()
    {
        var chunks = Create();
        Assert.IsTrue(SnapshotVerification.IsComplete([chunks[1], chunks[0]]));
        Assert.IsFalse(SnapshotVerification.IsComplete([chunks[0]]));
        Assert.IsFalse(SnapshotVerification.IsComplete([chunks[0], chunks[0]]));
        Assert.IsFalse(SnapshotVerification.IsComplete([chunks[0], chunks[1] with { Items = [chunks[1].Items[0] with { Version = "changed" }] }]));
        Assert.IsFalse(SnapshotVerification.IsComplete([chunks[0], chunks[1] with { ObservedAt = chunks[1].ObservedAt.AddSeconds(1) }]));
        Assert.IsFalse(SnapshotVerification.IsComplete([chunks[0], chunks[1] with { PartialReason = InventoryPartialReason.AccessDenied }]));
    }

    [TestMethod]
    public void Verify_RejectsDuplicateKeysAcrossChunksEvenWithMatchingDigest()
    {
        var chunks = Create(duplicate: true);
        Assert.IsFalse(SnapshotVerification.IsComplete(chunks));
    }

    private static InventoryChunk[] Create(bool duplicate = false)
    {
        var now = DateTimeOffset.UtcNow;
        var source = Guid.CreateVersion7();
        InventoryEntry[][] entries = [[new("a", "a", "1", "amd64")], [new(duplicate ? "a" : "b", "b", "2", "amd64")]];
        var measured = entries.Select(InventoryDigest.MeasureChunk).ToArray();
        var digest = InventoryDigest.Combine(measured.Select(item => item.Digest).ToArray());
        var total = measured.Sum(item => (long)item.Bytes);
        return Enumerable.Range(0, 2).Select(index => new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), now,
            source, 1, InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, index, 2, 2, total, digest, entries[index])).ToArray();
    }
}
