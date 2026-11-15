using System.Formats.Tar;
using SharpCompress.Compressors.Xz;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Streams the Canonical archive without extracting it to disk. The caller must keep the seekable input immutable
/// across both passes and keep staged results unavailable until complete validation succeeds. The callback receives the
/// original document bytes so storage can hash and retain the exact upstream evidence without serializing the parsed model.</summary>
internal static class UbuntuOsvArchive
{
    internal static async Task<int> ReadAsync(Stream compressed, long maximumExpandedBytes, int maximumEntries, int maximumDocumentBytes,
        Func<UbuntuOsvRecord, ReadOnlyMemory<byte>, CancellationToken, ValueTask> stage, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        ArgumentNullException.ThrowIfNull(stage);
        if (!compressed.CanRead || !compressed.CanSeek || maximumExpandedBytes <= 0 || maximumEntries <= 0 || maximumDocumentBytes <= 0)
            throw new ArgumentException("Invalid Ubuntu archive input or limits.");
        var start = compressed.Position;
        using (var preflightXz = new XZStream(compressed))
        using (var preflight = new ExpandedBudgetStream(preflightXz, maximumExpandedBytes))
        {
            await RepositoryTarPreflight.ValidateTarAsync(preflight, maximumExpandedBytes, token);
            await DrainPaddingAsync(preflight, token);
            if (compressed.Position != compressed.Length) throw new InvalidDataException("Unexpected data after XZ archive.");
        }
        compressed.Position = start;
        using var xz = new XZStream(compressed);
        using var bounded = new ExpandedBudgetStream(xz, maximumExpandedBytes);
        using var tar = new TarReader(bounded, leaveOpen: true);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entryCount = 0;
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, token)) is not null)
        {
            if (++entryCount > maximumEntries || !SafePath(entry.Name) || entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Invalid or oversized Ubuntu archive.");
            if (entry.EntryType == TarEntryType.Directory || !entry.Name.StartsWith("osv/cve/", StringComparison.Ordinal)) continue;
            if (entry.Length <= 0 || entry.Length > maximumDocumentBytes || entry.DataStream is null)
                throw new InvalidDataException("Invalid Ubuntu CVE document size.");
            var path = entry.Name.AsSpan();
            var slash = path.LastIndexOf('/');
            if (slash < 0 || !path[(slash + 1)..].EndsWith(".json", StringComparison.Ordinal)) throw new InvalidDataException("Invalid Ubuntu CVE path.");
            var filename = path[(slash + 1)..^5].ToString();
            if (!ids.Add(filename)) throw new InvalidDataException("Duplicate Ubuntu CVE record.");
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
            await entry.DataStream.ReadExactlyAsync(bytes, token);
            var record = UbuntuOsvReader.Read(bytes, maximumDocumentBytes);
            if (!string.Equals(filename, record.Id, StringComparison.Ordinal)) throw new InvalidDataException("Ubuntu CVE filename and identifier differ.");
            await stage(record, bytes, token);
        }
        if (ids.Count == 0) throw new InvalidDataException("Ubuntu archive contains no CVE records.");
        // TAR may end before the XZ footer. Drain through EOF to validate XZ checks and reject non-padding payload.
        await DrainPaddingAsync(bounded, token);
        if (compressed.Position != compressed.Length) throw new InvalidDataException("Unexpected data after XZ archive.");
        return ids.Count;
    }

    private static async Task DrainPaddingAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
            if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Unexpected data after TAR end.");
    }

    private static bool SafePath(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 512 &&
        !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':') &&
        !name.Split('/').Any(segment => segment is "." or "..");

    private sealed class ExpandedBudgetStream(Stream inner, long limit) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Count(await inner.ReadAsync(buffer, token));
        private int Count(int read)
        {
            if (read > limit - consumed) throw new InvalidDataException("Ubuntu archive exceeds its expanded size limit.");
            consumed += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
