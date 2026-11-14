using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

internal sealed class SigmaLicenseException() : Exception("Sigma repository license is missing or changed.");

/// <summary>Validates a bounded archive without extracting its paths to the local file system.</summary>
internal static class SigmaArchive
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 50_000;

    internal static async Task<int> ReadAsync(Stream archive, string commit, int maximumRules,
        Func<string, byte[], SigmaRuleMetadata, CancellationToken, Task> accept, CancellationToken token)
    {
        if (!archive.CanSeek) throw new ArgumentException("Sigma archive must be seekable.", nameof(archive));
        await SigmaTarPreflight.ValidateAsync(archive, token);
        archive.Position = 0;
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var bounded = new LimitedStream(gzip, MaximumExpandedBytes);
        using var reader = new TarReader(bounded, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = $"sigma-{commit}/";
        var total = 0;
        var ruleCount = 0;
        var licenseVerified = false;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: token)) is not null)
        {
            if (++total > MaximumEntries) throw new InvalidDataException("Sigma archive contains too many entries.");
            if (entry is PaxGlobalExtendedAttributesTarEntry global)
            {
                if (total != 1 || global.GlobalExtendedAttributes.Count != 1 ||
                    !global.GlobalExtendedAttributes.TryGetValue("comment", out var archiveCommit) || archiveCommit != commit)
                    throw new InvalidDataException("Invalid Sigma archive metadata.");
                continue;
            }
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"Sigma archive contains an unsupported entry type: {entry.EntryType}.");
            var path = entry.Name;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length > 512 || path.Contains('\\') || path.Contains(':') ||
                path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or "..") || !paths.Add(path.TrimEnd('/')))
                throw new InvalidDataException("Sigma archive contains an invalid or duplicate path.");
            var relative = path[prefix.Length..];
            if (relative == "LICENSE")
            {
                if (entry.EntryType == TarEntryType.Directory || entry.Length is < 1 or > 65536 || entry.DataStream is null)
                    throw new InvalidDataException("Invalid Sigma repository license.");
                var license = new byte[checked((int)entry.Length)];
                await entry.DataStream.ReadExactlyAsync(license, token);
                licenseVerified = Convert.ToHexStringLower(SHA256.HashData(license)) == "4213ed7e21d9435b0aa0aa7d11016e6ab3e4664fa71738ff507306fadce79597";
                if (!licenseVerified) throw new SigmaLicenseException();
                continue;
            }
            var inRuleTree = relative.StartsWith("rules/", StringComparison.Ordinal) || relative.StartsWith("rules-emerging-threats/", StringComparison.Ordinal) ||
                relative.StartsWith("rules-threat-hunting/", StringComparison.Ordinal);
            var fileName = relative[(relative.LastIndexOf('/') + 1)..];
            if (inRuleTree && entry.EntryType != TarEntryType.Directory &&
                (fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Equals("COPYING", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("COPYING.", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".license", StringComparison.OrdinalIgnoreCase))) throw new SigmaLicenseException();
            if (entry.EntryType == TarEntryType.Directory || !inRuleTree || !path.EndsWith(".yml", StringComparison.Ordinal)) continue;
            if (++ruleCount > maximumRules || entry.Length is < 1 or > SigmaRuleValidator.MaximumRuleBytes || entry.DataStream is null)
                throw new InvalidDataException("Sigma rules exceed the configured limits.");
            var content = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(content, token);
            SigmaRuleMetadata metadata;
            try { metadata = SigmaRuleValidator.Validate(content); }
            catch (InvalidDataException error) { throw new InvalidDataException($"Invalid Sigma rule: {path}.", error); }
            if (metadata.Id is { Length: > 128 } || metadata.Author is { Length: > 1024 } ||
                metadata.Status is { Length: > 128 } || metadata.License is { Length: > 256 } ||
                metadata.Product is { Length: > 128 } || metadata.Category is { Length: > 128 } || metadata.Service is { Length: > 128 } ||
                metadata.Id is { } id && !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate Sigma metadata.");
            await accept(relative, content, metadata, token);
        }
        if (!licenseVerified) throw new SigmaLicenseException();
        if (ruleCount == 0) throw new InvalidDataException("Sigma archive contains no rules.");
        return ruleCount;
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
        private void Count(int n) { read += n; if (read > limit) throw new InvalidDataException("Sigma archive exceeds the expanded size limit."); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
