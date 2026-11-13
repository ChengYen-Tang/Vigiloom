using System.Text.Json;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>Canonical item sizes and ordered SHA-256 hashes for an integrity snapshot.</summary>
public static class IntegrityDigest
{
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<IntegrityItem>(IntegrityChunkCodec.MaximumItemsPerChunk);

    public static int MeasureEntry(IntegrityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return BoundedJson.Measure(item, Options);
    }

    public static (int Bytes, byte[] Digest) MeasureChunk(IReadOnlyList<IntegrityItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > IntegrityChunkCodec.MaximumItemsPerChunk) throw new ArgumentOutOfRangeException(nameof(items));
        return BoundedJson.MeasureHash(items, Options);
    }

    public static byte[] Combine(IReadOnlyList<byte[]> orderedChunkDigests) => ChunkDigests.Combine(orderedChunkDigests, IntegrityChunkCodec.MaximumChunks);
}
