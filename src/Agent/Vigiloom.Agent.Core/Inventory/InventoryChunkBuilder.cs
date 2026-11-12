using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Inventory;

/// <summary>Builds immutable, bounded transport batches for one already-collected source snapshot.</summary>
public static class InventoryChunkBuilder
{
    // Leave room for metadata and JSON encoding outside the items array.
    private const int TargetBytes = 768 * 1024;
    private const int MaximumSingleEntryBytes = 900 * 1024;

    public static IReadOnlyList<QueuedBatch> Build(Guid snapshotId, long generation, InventorySource source,
        DateTimeOffset observedAt, DateTimeOffset enqueuedAt, InventoryOutcome outcome, IReadOnlyList<InventoryEntry> entries, InventoryPartialReason? partialReason = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > InventoryChunkCodec.MaximumSnapshotItems) throw new ArgumentOutOfRangeException(nameof(entries));
        var chunks = new List<IReadOnlyList<InventoryEntry>>();
        var current = new List<InventoryEntry>(InventoryChunkCodec.MaximumItemsPerChunk);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var size = 2; // JSON array brackets.
        long completedBytes = 0;
        var memberCount = 0;
        foreach (var item in entries)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || !keys.Add(item.Key))
                throw new ArgumentException("Inventory keys must be present and unique within the source.", nameof(entries));
            memberCount += item.Members?.Count ?? 0;
            if (memberCount > InventoryChunkCodec.MaximumSnapshotMembers)
                throw new ArgumentException("Inventory snapshot has too many group members.", nameof(entries));
            var measured = InventoryDigest.MeasureEntry(item);
            if (measured > MaximumSingleEntryBytes) throw new ArgumentException("One inventory entry exceeds the chunk limit.", nameof(entries));
            var nextSize = size + measured + (current.Count == 0 ? 0 : 1);
            if (current.Count > 0 && (current.Count == InventoryChunkCodec.MaximumItemsPerChunk || nextSize > TargetBytes))
            {
                if (chunks.Count >= InventoryChunkCodec.MaximumChunks - 1)
                    throw new ArgumentException("Too many inventory chunks.", nameof(entries));
                completedBytes += size;
                chunks.Add(current.ToArray());
                current.Clear();
                size = 2;
            }
            size += measured + (current.Count == 0 ? 0 : 1);
            if (completedBytes + size > InventoryChunkCodec.MaximumSnapshotBytes)
                throw new ArgumentException("Inventory snapshot exceeds its size limit.", nameof(entries));
            current.Add(item);
        }
        if (current.Count > 0 || chunks.Count == 0) chunks.Add(current.ToArray());
        if (chunks.Count > InventoryChunkCodec.MaximumChunks) throw new ArgumentException("Too many inventory chunks.", nameof(entries));
        var measurements = chunks.Select(InventoryDigest.MeasureChunk).ToArray();
        var totalBytes = measurements.Sum(value => (long)value.Bytes);
        if (totalBytes > InventoryChunkCodec.MaximumSnapshotBytes) throw new ArgumentException("Inventory snapshot exceeds its size limit.", nameof(entries));
        var digest = InventoryDigest.Combine(measurements.Select(value => value.Digest).ToArray());
        var result = new List<QueuedBatch>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var batchId = Guid.CreateVersion7();
            var schema = source > InventorySource.HostVolumes ? InventoryChunkCodec.ProcessSchemaVersion : InventoryChunkCodec.SchemaVersion;
            var chunk = new InventoryChunk(schema, batchId, enqueuedAt, snapshotId, generation, source,
                observedAt, outcome, index, chunks.Count, entries.Count, totalBytes, digest, chunks[index], partialReason);
            var bytes = InventoryChunkCodec.Serialize(chunk);
            result.Add(new(batchId, enqueuedAt, Math.Max(1, chunks[index].Count), bytes));
        }
        return result;
    }
}
