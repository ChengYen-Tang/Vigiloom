using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using System.Collections;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryChunkBuilderTests
{
    [TestMethod]
    public void Build_SplitsAndProtectsACompleteSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Guid.CreateVersion7();
        var items = Enumerable.Range(0, 105)
            .Select(i => new InventoryEntry($"package-{i}", $"package-{i}", "1:1.0-1", "x86_64"))
            .ToArray();
        var batches = InventoryChunkBuilder.Build(snapshot, 7, InventorySource.LinuxRpm, now, now, InventoryOutcome.Complete, items);
        Assert.HasCount(2, batches);
        var chunks = new List<InventoryChunk>();
        foreach (var batch in batches)
        {
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var parsed));
            Assert.IsNotNull(parsed);
            chunks.Add(parsed);
            Assert.AreEqual(parsed.Items.Count, batch.RecordCount);
            Assert.AreEqual(snapshot, parsed.SnapshotId);
            Assert.AreEqual(105, parsed.TotalItems);
        }
        Assert.AreEqual(0, chunks[0].ChunkIndex);
        Assert.AreEqual(1, chunks[1].ChunkIndex);
        var measured = chunks.Select(chunk => InventoryDigest.MeasureChunk(chunk.Items)).ToArray();
        Assert.AreEqual(measured.Sum(item => (long)item.Bytes), chunks[0].TotalBytes);
        CollectionAssert.AreEqual(InventoryDigest.Combine(measured.Select(item => item.Digest).ToArray()), chunks[0].Digest);
        CollectionAssert.AreEqual(chunks[0].Digest, chunks[1].Digest);
    }

    [TestMethod]
    public void Build_EmptySnapshotAndDuplicateKeysHaveExplicitOutcomes()
    {
        var now = DateTimeOffset.UtcNow;
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg,
            now, now, InventoryOutcome.Complete, []);
        Assert.HasCount(1, batches);
        Assert.AreEqual(1, batches[0].RecordCount);
        Assert.IsTrue(InventoryChunkCodec.TryRead(batches[0].Payload, out var empty));
        Assert.IsNotNull(empty);
        Assert.AreEqual(0, empty.TotalItems);
        Assert.IsEmpty(empty.Items);
        var duplicate = new InventoryEntry("same", "same", "1", "amd64");
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1,
            InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, [duplicate, duplicate]));
    }

    [TestMethod]
    public void Build_SplitsLargeEntriesBeforeTheTransportLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var members = Enumerable.Range(0, 40).Select(i => $"{i}-{new string('組', 200)}").ToArray();
        var groups = Enumerable.Range(0, 20).Select(i => new InventoryEntry($"group-{i}", $"group-{i}", NumericId: (uint)i, Members: members)).ToArray();
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.LinuxGroups, now, now, InventoryOutcome.Complete, groups);
        Assert.IsGreaterThan(1, batches.Count);
        foreach (var batch in batches)
        {
            Assert.IsLessThanOrEqualTo(Vigiloom.Contracts.Agent.AgentBatchCodec.MaximumBytes, batch.Payload.Length);
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out _));
        }
    }

    [TestMethod]
    public void Build_StopsEnumeratingWhenSnapshotIsTooLarge()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new LargeInventory();
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1,
            InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, entries));
        Assert.IsLessThan(entries.Count, entries.ReadCount);
    }

    [TestMethod]
    public void Build_RejectsTooManyMembersAcrossDifferentGroups()
    {
        var now = DateTimeOffset.UtcNow;
        var members = Enumerable.Repeat("member", InventoryChunkCodec.MaximumMembers).ToArray();
        var groups = Enumerable.Range(0, 21)
            .Select(i => new InventoryEntry($"group-{i}", $"group-{i}", NumericId: (uint)i, Members: members)).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1,
            InventorySource.LinuxGroups, now, now, InventoryOutcome.Complete, groups));
    }

    private sealed class LargeInventory : IReadOnlyList<InventoryEntry>
    {
        private static readonly string LargeVersion = new('版', 2048);
        public int Count => 20_000;
        public int ReadCount { get; private set; }
        public InventoryEntry this[int index] => new($"p{index}", $"p{index}", LargeVersion, "amd64");
        public IEnumerator<InventoryEntry> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) { ReadCount++; yield return this[i]; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
