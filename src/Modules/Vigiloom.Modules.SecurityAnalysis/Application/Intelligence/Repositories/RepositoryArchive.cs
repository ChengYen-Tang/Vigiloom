using System.Formats.Tar;
using System.IO.Compression;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

/// <summary>Streams a bounded, immutable GitHub archive without extracting files to disk.</summary>
internal static class RepositoryArchive
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 50_000;

    internal static async Task ReadAsync(Stream archive, string repositoryName, string commit,
        Func<string, TarEntry, CancellationToken, Task> accept, CancellationToken token)
    {
        if (!archive.CanSeek) throw new ArgumentException("Repository archive must be seekable.", nameof(archive));
        await RepositoryTarPreflight.ValidateAsync(archive, token);
        archive.Position = 0;
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var bounded = new LimitedStream(gzip, MaximumExpandedBytes);
        using var reader = new TarReader(bounded, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var prefix = $"{repositoryName}-{commit}/";
        var total = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: token)) is not null)
        {
            if (++total > MaximumEntries) throw new InvalidDataException("Repository archive contains too many entries.");
            if (entry is PaxGlobalExtendedAttributesTarEntry global)
            {
                if (total != 1 || global.GlobalExtendedAttributes.Count != 1 ||
                    !global.GlobalExtendedAttributes.TryGetValue("comment", out var archiveCommit) || archiveCommit != commit)
                    throw new InvalidDataException("Invalid repository archive metadata.");
                continue;
            }
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"Repository archive contains an unsupported entry type: {entry.EntryType}.");
            var path = entry.Name;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length > 512 || path.Contains('\\') || path.Contains(':') ||
                path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or "..") || !paths.Add(path.TrimEnd('/')))
                throw new InvalidDataException("Repository archive contains an invalid or duplicate path.");
            await accept(path[prefix.Length..], entry, token);
        }
    }

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count(n); return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); Count(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var n = await inner.ReadAsync(buffer, token); Count(n); return n; }
        private void Count(int n) { read += n; if (read > limit) throw new InvalidDataException("Repository archive exceeds the expanded size limit."); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
