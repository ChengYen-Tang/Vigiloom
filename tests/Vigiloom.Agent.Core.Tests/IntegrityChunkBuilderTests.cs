using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class IntegrityChunkBuilderTests
{
    [TestMethod]
    public void Build_ProducesOrderedImmutableChunksForOneScope()
    {
        var items = Enumerable.Range(0, 101).Select(i => new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, [$"file-{i}"]), IntegrityItemKind.File, new string('a', 64))).ToArray();
        var now = DateTimeOffset.UtcNow;
        var scope = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var batches = IntegrityChunkBuilder.Build(scope, 3, snapshot, 9, now, now, IntegrityScanOutcome.Complete, items);
        Assert.HasCount(2, batches);
        var chunks = batches.Select(batch =>
        {
            Assert.IsTrue(IntegrityChunkCodec.TryRead(batch.Payload, out var chunk));
            return chunk!;
        }).ToArray();
        Assert.IsTrue(chunks.All(x => x.ScopeId == scope && x.ScopeRevision == 3 && x.SnapshotId == snapshot && x.Generation == 9));
        Assert.AreEqual(0, chunks[0].ChunkIndex);
        Assert.AreEqual(1, chunks[1].ChunkIndex);
        CollectionAssert.AreEqual(chunks[0].Digest, IntegrityDigest.Combine(chunks.Select(x => IntegrityDigest.MeasureChunk(x.Items).Digest).ToArray()));
    }

    [TestMethod]
    public void Build_RejectsDuplicateKeysAndRepresentsACompleteEmptyScope()
    {
        var now = DateTimeOffset.UtcNow;
        var scope = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var entry = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["one"]), IntegrityItemKind.File, new string('a', 64));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkBuilder.Build(scope, 1, snapshot, 1, now, now, IntegrityScanOutcome.Complete, [entry, entry]));
        var batches = IntegrityChunkBuilder.Build(scope, 1, snapshot, 1, now, now, IntegrityScanOutcome.Complete, []);
        Assert.HasCount(1, batches);
        Assert.IsTrue(IntegrityChunkCodec.TryRead(batches[0].Payload, out var parsed));
        Assert.AreEqual(0, parsed!.TotalItems);
        Assert.AreEqual(1, batches[0].RecordCount);
    }
}
