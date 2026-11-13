using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vigiloom.Agent.Core.Integrity;

/// <summary>Hashes registry type and bytes using a versioned, unambiguous frame.</summary>
public static class IntegrityHash
{
    /// <summary>Hashes no more than the observed size; premature EOF makes the observation incomplete.</summary>
    public static async Task<(byte[]? Digest, long BytesRead)> FileAsync(Stream stream, long length, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long read = 0;
            while (read < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(64 * 1024, length - read)), token);
                if (count == 0) return (null, read);
                hash.AppendData(buffer, 0, count);
                read += count;
            }
            return (hash.GetHashAndReset(), read);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static string RegistryValue(uint nativeType, ReadOnlySpan<byte> rawValue)
    {
        Span<byte> header = stackalloc byte[13];
        header[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header[1..5], nativeType);
        BinaryPrimitives.WriteUInt64BigEndian(header[5..], (ulong)rawValue.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(rawValue);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
