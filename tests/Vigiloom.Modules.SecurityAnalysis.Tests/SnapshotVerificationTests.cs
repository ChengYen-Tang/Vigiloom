using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class SnapshotVerificationTests
{
    [TestMethod]
    public void Verify_RequiresOneUpdateSourceAcrossRelationshipChunks()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Guid.CreateVersion7();
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var first = new InventoryEntry($"{firstId:D}:1", "first", UpdateId: firstId, UpdateRevision: 1, UpdateInstalled: true,
            UpdateRebootRequired: false, UpdateCves: [], UpdateKbs: [], UpdateServerSelection: 0, UpdateServiceId: Guid.Empty, UpdateSupersededIds: []);
        var second = first with { Key = $"{secondId:D}:1", Name = "second", UpdateId = secondId };
        Assert.IsTrue(SnapshotVerification.IsComplete(Build(first, second)));
        Assert.IsFalse(SnapshotVerification.IsComplete(Build(first, second with { UpdateServerSelection = 2 })));
        Assert.IsFalse(SnapshotVerification.IsComplete(Build(first, second with { UpdateServiceId = Guid.CreateVersion7() })));

        InventoryChunk[] Build(InventoryEntry a, InventoryEntry b)
        {
            InventoryEntry[][] entries = [[a], [b]];
            var measured = entries.Select(InventoryDigest.MeasureChunk).ToArray();
            var digest = InventoryDigest.Combine(measured.Select(x => x.Digest).ToArray());
            return Enumerable.Range(0, 2).Select(i => new InventoryChunk(InventoryChunkCodec.WindowsUpdateRelationshipsSchemaVersion,
                Guid.CreateVersion7(), now, snapshot, 1, InventorySource.WindowsUpdate, now, InventoryOutcome.Partial, i, 2, 2,
                measured.Sum(x => (long)x.Bytes), digest, entries[i])).ToArray();
        }
    }

    [TestMethod]
    public void Verify_RejectsUpdateReferencesOverLimitAcrossValidChunks()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Guid.CreateVersion7();
        var references = Enumerable.Repeat("CVE-2025-1234", 5_000).ToArray();
        var entries = Enumerable.Range(0, 2).Select(part => Enumerable.Range(0, 11).Select(i =>
        {
            var id = Guid.CreateVersion7();
            return new InventoryEntry($"{id:D}:1", $"update-{part}-{i}", UpdateId: id, UpdateRevision: 1,
                UpdateInstalled: true, UpdateRebootRequired: false, UpdateCves: references, UpdateKbs: []);
        }).ToArray()).ToArray();
        var measured = entries.Select(InventoryDigest.MeasureChunk).ToArray();
        var digest = InventoryDigest.Combine(measured.Select(x => x.Digest).ToArray());
        var total = measured.Sum(x => (long)x.Bytes);
        var chunks = Enumerable.Range(0, 2).Select(i => new InventoryChunk(InventoryChunkCodec.WindowsUpdateSchemaVersion,
            Guid.CreateVersion7(), now, snapshot, 1, InventorySource.WindowsUpdate, now, InventoryOutcome.Partial,
            i, 2, 22, total, digest, entries[i])).ToArray();
        foreach (var chunk in chunks) Assert.IsTrue(InventoryChunkCodec.TryRead(InventoryChunkCodec.Serialize(chunk), out _));
        Assert.IsFalse(SnapshotVerification.IsComplete(chunks));
    }
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
