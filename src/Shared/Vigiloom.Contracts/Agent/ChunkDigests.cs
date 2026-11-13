using System.Security.Cryptography;

namespace Vigiloom.Contracts.Agent;

/// <summary>Combines a bounded ordered sequence of SHA-256 chunk digests.</summary>
internal static class ChunkDigests
{
    public static byte[] Combine(IReadOnlyList<byte[]> ordered, int maximumChunks)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Count < 1 || ordered.Count > maximumChunks) throw new ArgumentOutOfRangeException(nameof(ordered));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var digest in ordered)
        {
            if (digest is not { Length: 32 }) throw new ArgumentException("Each chunk digest must be SHA-256.", nameof(ordered));
            hash.AppendData(digest);
        }
        return hash.GetHashAndReset();
    }
}
