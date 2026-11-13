using System.Security.Cryptography;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

internal static class IntegrityManifest
{
    internal static bool Matches(IntegrityScan scan, IntegrityOwner owner, IntegrityChunk chunk) =>
        scan.AgentId == owner.AgentId && scan.DeviceId == owner.DeviceId && scan.DeviceGeneration == owner.DeviceGeneration &&
        scan.ScopeId == chunk.ScopeId && scan.ScopeRevision == chunk.ScopeRevision && scan.Generation == chunk.Generation &&
        scan.SnapshotId == chunk.SnapshotId && scan.EnqueuedAt == chunk.EnqueuedAt && scan.ObservedAt == chunk.ObservedAt &&
        scan.Outcome == chunk.Outcome && scan.CoverageGap == chunk.CoverageGap && scan.PartialReason == chunk.PartialReason &&
        scan.ChunkCount == chunk.ChunkCount && scan.TotalItems == chunk.TotalItems && scan.TotalBytes == chunk.TotalBytes &&
        CryptographicOperations.FixedTimeEquals(scan.Digest, chunk.Digest);

    internal static bool Verify(IntegrityScan scan, IReadOnlyList<IntegrityChunk> chunks, IntegrityOwner owner, StringComparer? keyComparer = null)
    {
        if (chunks.Count != scan.ChunkCount) return false;
        var keys = new HashSet<string>(keyComparer ?? StringComparer.Ordinal);
        var digests = new List<byte[]>(chunks.Count);
        long bytes = 0;
        var count = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (chunk.ChunkIndex != index || !Matches(scan, owner, chunk)) return false;
            foreach (var item in chunk.Items)
                if (!keys.Add(item.Key)) return false;
            var measured = IntegrityDigest.MeasureChunk(chunk.Items);
            bytes += measured.Bytes;
            count += chunk.Items.Count;
            if (bytes > scan.TotalBytes || count > scan.TotalItems) return false;
            digests.Add(measured.Digest);
        }
        return bytes == scan.TotalBytes && count == scan.TotalItems &&
            CryptographicOperations.FixedTimeEquals(IntegrityDigest.Combine(digests), scan.Digest);
    }
}
