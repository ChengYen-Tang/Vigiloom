using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

/// <summary>Bounds TAR metadata before TarReader can allocate an extended-header buffer.</summary>
internal static class SigmaTarPreflight
{
    private const int BlockSize = 512;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumMetadataBytes = 64 * 1024;

    internal static async Task ValidateAsync(Stream archive, CancellationToken token)
    {
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long total = 0;
            var zeroBlocks = 0;
            while (true)
            {
                await gzip.ReadExactlyAsync(buffer.AsMemory(0, BlockSize), token);
                total += BlockSize;
                if (total > MaximumExpandedBytes) throw new InvalidDataException("Sigma archive exceeds the expanded size limit.");
                if (buffer.AsSpan(0, BlockSize).IndexOfAnyExcept((byte)0) < 0)
                {
                    if (++zeroBlocks == 2) return;
                    continue;
                }
                if (zeroBlocks != 0) throw new InvalidDataException("Invalid TAR terminator.");
                var size = ParseSize(buffer.AsSpan(124, 12));
                var kind = buffer[156];
                if (kind is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K' && size > MaximumMetadataBytes)
                    throw new InvalidDataException("Sigma TAR metadata exceeds the limit.");
                var remaining = checked((size + BlockSize - 1) / BlockSize * BlockSize);
                if (remaining > MaximumExpandedBytes - total) throw new InvalidDataException("Sigma archive exceeds the expanded size limit.");
                if (kind is (byte)'x' or (byte)'g')
                {
                    await gzip.ReadExactlyAsync(buffer.AsMemory(0, (int)size), token);
                    ValidatePax(buffer.AsSpan(0, (int)size));
                    total += size;
                    remaining -= size;
                }
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    await gzip.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                    remaining -= count;
                    total += count;
                }
            }
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Sigma archive.", error); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidatePax(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var separator = data.IndexOf((byte)' ');
            if (separator < 1 || !Utf8Parser.TryParse(data[..separator], out int length, out var used) || used != separator ||
                length <= separator + 2 || length > data.Length || data[length - 1] != (byte)'\n')
                throw new InvalidDataException("Invalid TAR extended metadata.");
            var record = data[(separator + 1)..(length - 1)];
            var equals = record.IndexOf((byte)'=');
            if (equals < 1) throw new InvalidDataException("Invalid TAR extended metadata.");
            var key = record[..equals];
            if (key.SequenceEqual("size"u8) || key.StartsWith("GNU.sparse"u8))
                throw new InvalidDataException("TAR size overrides are not supported.");
            data = data[length..];
        }
    }

    private static long ParseSize(ReadOnlySpan<byte> field)
    {
        long size = 0;
        var ended = false;
        var started = false;
        foreach (var value in field)
        {
            if (value == 0) { ended = true; continue; }
            if (value == (byte)' ') { if (started) ended = true; continue; }
            if (ended || value is < (byte)'0' or > (byte)'7') throw new InvalidDataException("Unsupported TAR size encoding.");
            started = true;
            size = checked(size * 8 + value - '0');
        }
        return size;
    }
}
