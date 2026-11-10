using System.Security.Cryptography;

namespace Vigiloom.Agent.Core.Logs;

public enum TextFileReadStatus { Data, End, CursorInvalidated }
public sealed record TextFileCursor(FileIdentity Identity, long Offset, string Anchor);
public sealed record TextFileReadResult(TextFileReadStatus Status, ReadOnlyMemory<byte> Data, TextFileCursor? NextCursor);

/// <summary>Reads a bounded byte chunk from an open file; parsing and durable advancement belong to the caller.</summary>
public static class TextFileSource
{
    private const int AnchorSize = 64;
    private const int MaximumChunkBytes = 64 * 1024;

    public static async Task<TextFileReadResult> ReadAsync(string path, TextFileCursor? cursor, CancellationToken token)
        => await ReadAsync(path, cursor, MaximumChunkBytes, token);

    /// <summary>Reads at most the requested bytes and produces a cursor for exactly the bytes returned.</summary>
    public static async Task<TextFileReadResult> ReadAsync(string path, TextFileCursor? cursor, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 1 or > MaximumChunkBytes + 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute log file path is required.", nameof(path));
        if (cursor is not null && (cursor.Identity is null || cursor.Offset < 0 || cursor.Anchor is not { Length: 64 } || !cursor.Anchor.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid file cursor.", nameof(cursor));
        await using var stream = OperatingSystem.IsLinux() ? LinuxFileIdentity.OpenRead(path, MaximumChunkBytes) :
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                MaximumChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var identity = FileIdentity.FromHandle(stream.SafeFileHandle);
        var offset = cursor?.Offset ?? 0;
        if (cursor is not null && (identity != cursor.Identity || stream.Length < offset ||
            !await AnchorMatchesAsync(stream, offset, cursor.Anchor, token))) return Invalidated();

        if (stream.Length == offset) return new(TextFileReadStatus.End, ReadOnlyMemory<byte>.Empty, cursor);

        stream.Position = offset;
        var buffer = new byte[maximumBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        // Detect replacement or rewriting of the previously acknowledged region while reading.
        if (cursor is not null && (stream.Length < offset || !await AnchorMatchesAsync(stream, offset, cursor.Anchor, token)))
            return Invalidated();
        if (count == 0) return new(TextFileReadStatus.End, ReadOnlyMemory<byte>.Empty, cursor);
        var nextOffset = checked(offset + count);
        var candidate = await ComputeCandidateAnchorAsync(stream, offset, buffer.AsMemory(0, count), token);
        var actual = await ComputeAnchorAsync(stream, nextOffset, token);
        if (candidate is null || actual is null || !CryptographicOperations.FixedTimeEquals(candidate, actual)) return Invalidated();
        if (cursor is not null && !await AnchorMatchesAsync(stream, offset, cursor.Anchor, token)) return Invalidated();
        return new(TextFileReadStatus.Data, buffer.AsMemory(0, count), new(identity, nextOffset, Convert.ToHexString(candidate)));
    }

    private static TextFileReadResult Invalidated() => new(TextFileReadStatus.CursorInvalidated, ReadOnlyMemory<byte>.Empty, null);

    private static async Task<bool> AnchorMatchesAsync(FileStream stream, long offset, string expected, CancellationToken token)
    {
        var actual = await ComputeAnchorAsync(stream, offset, token);
        return actual is not null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual);
    }

    private static async Task<byte[]?> ComputeAnchorAsync(FileStream stream, long offset, CancellationToken token)
    {
        var length = (int)Math.Min(offset, AnchorSize);
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read), offset - length + read, token);
            if (count == 0) return null;
            read += count;
        }
        return SHA256.HashData(bytes);
    }

    private static async Task<byte[]?> ComputeCandidateAnchorAsync(FileStream stream, long offset, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var suffix = Math.Min(data.Length, AnchorSize);
        var prefix = (int)Math.Min(offset, AnchorSize - suffix);
        var bytes = new byte[prefix + suffix];
        var read = 0;
        while (read < prefix)
        {
            var count = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read, prefix - read), offset - prefix + read, token);
            if (count == 0) return null;
            read += count;
        }
        data.Span[^suffix..].CopyTo(bytes.AsSpan(prefix));
        return SHA256.HashData(bytes);
    }
}
