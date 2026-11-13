using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Integrity;

/// <summary>Builds immutable bounded batches for one observed scope generation.</summary>
public static class IntegrityChunkBuilder
{
    private const int TargetBytes = 768 * 1024;
    private const int MaximumSingleEntryBytes = 900 * 1024;

    public static IReadOnlyList<QueuedBatch> Build(Guid scopeId, long scopeRevision, Guid snapshotId, long generation,
        DateTimeOffset observedAt, DateTimeOffset enqueuedAt, IntegrityScanOutcome outcome, IReadOnlyList<IntegrityItem> entries,
        bool coverageGap = false, IntegrityPartialReason? partialReason = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > IntegrityChunkCodec.MaximumSnapshotItems) throw new ArgumentOutOfRangeException(nameof(entries));
        var chunks = new List<IReadOnlyList<IntegrityItem>>();
        var current = new List<IntegrityItem>(IntegrityChunkCodec.MaximumItemsPerChunk);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var size = 2;
        long completedBytes = 0;
        foreach (var item in entries)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || !keys.Add(item.Key))
                throw new ArgumentException("Integrity keys must be present and unique within the scope.", nameof(entries));
            var measured = IntegrityDigest.MeasureEntry(item);
            if (measured > MaximumSingleEntryBytes) throw new ArgumentException("One integrity entry exceeds the chunk limit.", nameof(entries));
            var nextSize = size + measured + (current.Count == 0 ? 0 : 1);
            if (current.Count > 0 && (current.Count == IntegrityChunkCodec.MaximumItemsPerChunk || nextSize > TargetBytes))
            {
                if (chunks.Count >= IntegrityChunkCodec.MaximumChunks - 1) throw new ArgumentException("Too many integrity chunks.", nameof(entries));
                completedBytes += size;
                chunks.Add(current.ToArray());
                current.Clear();
                size = 2;
            }
            size += measured + (current.Count == 0 ? 0 : 1);
            if (completedBytes + size > IntegrityChunkCodec.MaximumSnapshotBytes) throw new ArgumentException("Integrity snapshot exceeds its size limit.", nameof(entries));
            current.Add(item);
        }
        if (current.Count > 0 || chunks.Count == 0) chunks.Add(current.ToArray());
        var measurements = chunks.Select(IntegrityDigest.MeasureChunk).ToArray();
        var totalBytes = measurements.Sum(x => (long)x.Bytes);
        if (totalBytes > IntegrityChunkCodec.MaximumSnapshotBytes) throw new ArgumentException("Integrity snapshot exceeds its size limit.", nameof(entries));
        var digest = IntegrityDigest.Combine(measurements.Select(x => x.Digest).ToArray());
        var batches = new List<QueuedBatch>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var id = Guid.CreateVersion7();
            var chunk = new IntegrityChunk(IntegrityChunkCodec.SchemaVersion, id, enqueuedAt, scopeId, scopeRevision, snapshotId,
                generation, observedAt, outcome, coverageGap, partialReason, index, chunks.Count, entries.Count, totalBytes, digest, chunks[index]);
            batches.Add(new(id, enqueuedAt, Math.Max(1, chunks[index].Count), IntegrityChunkCodec.Serialize(chunk)));
        }
        return batches;
    }
}
