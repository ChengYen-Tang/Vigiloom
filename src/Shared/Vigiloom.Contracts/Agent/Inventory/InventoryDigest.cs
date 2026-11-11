using System.Security.Cryptography;
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

    public static byte[] Combine(IReadOnlyList<byte[]> orderedChunkDigests)
    {
        ArgumentNullException.ThrowIfNull(orderedChunkDigests);
        if (orderedChunkDigests.Count is < 1 or > InventoryChunkCodec.MaximumChunks)
            throw new ArgumentOutOfRangeException(nameof(orderedChunkDigests));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var digest in orderedChunkDigests)
        {
            if (digest is not { Length: 32 }) throw new ArgumentException("Each chunk digest must be SHA-256.", nameof(orderedChunkDigests));
            hash.AppendData(digest);
        }
        return hash.GetHashAndReset();
    }
}
