using System.Security.Cryptography;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Verifies a complete staged generation before it becomes the current source snapshot.</summary>
internal static class SnapshotVerification
{
    internal static bool IsComplete(IReadOnlyList<InventoryChunk> chunks)
    {
        if (chunks.Count is < 1 or > InventoryChunkCodec.MaximumChunks) return false;
        var ordered = chunks.OrderBy(chunk => chunk.ChunkIndex).ToArray();
        var manifest = ordered[0];
        if (ordered.Length != manifest.ChunkCount) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var digests = new List<byte[]>(ordered.Length);
        long bytes = 0;
        var count = 0;
        var memberCount = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var chunk = ordered[index];
            if (chunk.ChunkIndex != index || chunk.SchemaVersion != manifest.SchemaVersion || chunk.SnapshotId != manifest.SnapshotId ||
                chunk.Generation != manifest.Generation || chunk.Source != manifest.Source || chunk.ObservedAt != manifest.ObservedAt ||
                chunk.Outcome != manifest.Outcome || chunk.ChunkCount != manifest.ChunkCount || chunk.TotalItems != manifest.TotalItems ||
                chunk.TotalBytes != manifest.TotalBytes || !CryptographicOperations.FixedTimeEquals(chunk.Digest, manifest.Digest)) return false;
            foreach (var item in chunk.Items)
            {
                if (!keys.Add(item.Key)) return false;
                memberCount += item.Members?.Count ?? 0;
                if (memberCount > InventoryChunkCodec.MaximumSnapshotMembers) return false;
            }
            var measured = InventoryDigest.MeasureChunk(chunk.Items);
            bytes += measured.Bytes;
            count += chunk.Items.Count;
            if (bytes > manifest.TotalBytes || count > manifest.TotalItems) return false;
            digests.Add(measured.Digest);
        }
        return bytes == manifest.TotalBytes && count == manifest.TotalItems &&
            CryptographicOperations.FixedTimeEquals(InventoryDigest.Combine(digests), manifest.Digest);
    }
}
