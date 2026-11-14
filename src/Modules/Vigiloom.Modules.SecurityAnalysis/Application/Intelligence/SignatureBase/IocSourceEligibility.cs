using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

/// <summary>Validates an attributed IOC source subset, without authorizing YARA rules.</summary>
internal static class IocSourceEligibility
{
    internal const string HashPath = "iocs/hash-iocs.txt";
    internal const string C2Path = "iocs/c2-iocs.txt";
    internal const string PolicyVersion = "signature-base-ioc-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] LicenseNotices = ["license", "licence", "spdx", "all rights reserved", "non-commercial", "noncommercial",
        "not for commercial", "no redistribution", "do not redistribute", "gpl-", "gpl ", "cc-by", "cc by", "apache-2.0", "bsd-3-clause"];

    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token) =>
        await AssessAsync(db, sourceId, revisionId, token) is not null;

    internal static async Task<IocAssessment?> AssessAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.SchemaVersion != SignatureRefresh.SchemaVersion ||
            revision.MappingVersion != SignatureRefresh.MappingVersion || revision.LicenseEvidence != SignatureRefresh.LicenseEvidence ||
            revision.UpstreamVersion.Length != 40 || !revision.UpstreamVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(revision.Artifact), revision.Digest) ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            await db.SignatureFiles.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount ||
            await db.SignatureFiles.AnyAsync(x => x.RevisionId == revisionId && x.Kind == (int)SignatureFileKind.LicenseEvidence && x.Path.StartsWith("iocs/"), token)) return null;

        var files = await db.SignatureFiles.AsNoTracking().Where(x => x.RevisionId == revisionId &&
            (x.Path == "LICENSE" || x.Path == "README.md" || x.Path == HashPath || x.Path == C2Path))
            .OrderBy(x => x.Path).Select(x => new { x.Path, x.Kind, x.Content, x.Digest }).ToArrayAsync(token);
        if (files.Length != 4) return null;
        var indicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupported = 0;
        var hashCount = 0;
        var c2Count = 0;
        using var digestBuilder = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digestBuilder.AppendData(Encoding.UTF8.GetBytes(PolicyVersion + "\0" + revision.UpstreamVersion));
        foreach (var file in files)
        {
            var digest = SHA256.HashData(file.Content);
            if (!CryptographicOperations.FixedTimeEquals(digest, file.Digest)) return null;
            if (file.Path == "LICENSE" && (file.Kind != (int)SignatureFileKind.LicenseEvidence || Convert.ToHexStringLower(digest) != SignatureArchive.LicenseHash)) return null;
            if (file.Path == "README.md" && (file.Kind != (int)SignatureFileKind.LicenseEvidence || Convert.ToHexStringLower(digest) != SignatureArchive.ReadmeHash)) return null;
            if (file.Path is HashPath or C2Path)
            {
                if (file.Kind != (int)SignatureFileKind.Ioc || ContainsLicenseNotice(file.Path, file.Content)) return null;
                try
                {
                    var before = indicators.Count;
                    foreach (var entry in IocParser.Parse(file.Path, file.Content))
                        if (entry.Reason is null) indicators.Add(entry.Kind + ":" + entry.Value); else unsupported++;
                    if (file.Path == HashPath) hashCount = indicators.Count - before;
                    else c2Count = indicators.Count - before;
                }
                catch (InvalidDataException) { return null; }
            }
            digestBuilder.AppendData(Encoding.UTF8.GetBytes(file.Path + "\0"));
            digestBuilder.AppendData(digest);
        }
        var valid = indicators.Count;
        if (valid == 0 || !files.Any(x => x.Path == "LICENSE") || !files.Any(x => x.Path == "README.md") ||
            !files.Any(x => x.Path == HashPath) || !files.Any(x => x.Path == C2Path)) return null;
        Span<byte> counts = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(counts, valid);
        BinaryPrimitives.WriteInt32BigEndian(counts[4..], unsupported);
        BinaryPrimitives.WriteInt32BigEndian(counts[8..], hashCount);
        BinaryPrimitives.WriteInt32BigEndian(counts[12..], c2Count);
        digestBuilder.AppendData(counts);
        return new(valid, unsupported, hashCount, c2Count, Convert.ToHexStringLower(digestBuilder.GetHashAndReset()));
    }

    // Repository terms govern IOC data. A new explicit licensing notice needs review; normal attribution is preserved.
    private static bool ContainsLicenseNotice(string path, byte[] content)
    {
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException) { return true; }
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            var trimmed = line.AsSpan().TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var separator = path == HashPath ? trimmed.IndexOf(';') : -1;
                if (separator < 0) continue;
                trimmed = trimmed[(separator + 1)..];
            }
            foreach (var notice in LicenseNotices)
                if (trimmed.Contains(notice, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

internal sealed record IocAssessment(int ValidCount, int UnsupportedCount, int HashCount, int C2Count, string ManifestSha256);
