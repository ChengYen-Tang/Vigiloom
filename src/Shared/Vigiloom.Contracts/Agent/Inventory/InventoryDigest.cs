using System.Text.Json;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Hashes the ordered items of each schema-4 chunk and then the ordered chunk digests.</summary>
public static class InventoryDigest
{
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<InventoryEntry>(InventoryChunkCodec.MaximumItemsPerChunk);

    /// <summary>Measures one entry using the same JSON rules as a snapshot chunk.</summary>
    public static int MeasureEntry(InventoryEntry item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return BoundedJson.Measure(item, Options);
    }

    public static (int Bytes, byte[] Digest) MeasureChunk(IReadOnlyList<InventoryEntry> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > InventoryChunkCodec.MaximumItemsPerChunk) throw new ArgumentOutOfRangeException(nameof(items));
        return BoundedJson.MeasureHash(items, Options);
    }

    public static byte[] Combine(IReadOnlyList<byte[]> orderedChunkDigests) =>
        ChunkDigests.Combine(orderedChunkDigests, InventoryChunkCodec.MaximumChunks);
}
