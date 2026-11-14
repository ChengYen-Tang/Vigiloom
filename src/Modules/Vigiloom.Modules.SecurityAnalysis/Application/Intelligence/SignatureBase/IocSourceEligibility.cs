using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

/// <summary>Checks the two documented IOC file formats without authorizing YARA rules.</summary>
internal static class IocSourceEligibility
{
    internal const string HashPath = "iocs/hash-iocs.txt";
    internal const string C2Path = "iocs/c2-iocs.txt";
    internal static readonly IocSourcePolicy Reviewed = new(
        "c11894bdde311ce0deebcdf29bfa1d6dcad9fc545af2a1fc7dbc748a6b1eabfa",
        "d7116ac4aa0d7521376a6fd56f05194eacedefac54eefcff7ca49a0db233cc86");

    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token, IocSourcePolicy? policy = null)
    {
        policy ??= Reviewed;
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.SchemaVersion != SignatureRefresh.SchemaVersion ||
            revision.MappingVersion != SignatureRefresh.MappingVersion || revision.LicenseEvidence != SignatureRefresh.LicenseEvidence ||
            revision.UpstreamVersion.Length != 40 || !revision.UpstreamVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(revision.Artifact), revision.Digest) ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            await db.SignatureFiles.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount ||
            await db.SignatureFiles.AnyAsync(x => x.RevisionId == revisionId && x.Kind == (int)SignatureFileKind.LicenseEvidence && x.Path.StartsWith("iocs/"), token)) return false;

        var files = await db.SignatureFiles.AsNoTracking().Where(x => x.RevisionId == revisionId &&
            (x.Path == "LICENSE" || x.Path == "README.md" || x.Path == HashPath || x.Path == C2Path))
            .Select(x => new { x.Path, x.Kind, x.Content, x.Digest }).ToArrayAsync(token);
        if (files.Length != 4) return false;
        foreach (var file in files)
        {
            var digest = SHA256.HashData(file.Content);
            if (!CryptographicOperations.FixedTimeEquals(digest, file.Digest)) return false;
            if (file.Path == "LICENSE" && (file.Kind != (int)SignatureFileKind.LicenseEvidence || Convert.ToHexStringLower(digest) != SignatureArchive.LicenseHash)) return false;
            if (file.Path == "README.md" && (file.Kind != (int)SignatureFileKind.LicenseEvidence || Convert.ToHexStringLower(digest) != SignatureArchive.ReadmeHash)) return false;
            if (file.Path == HashPath && (file.Kind != (int)SignatureFileKind.Ioc || Convert.ToHexStringLower(digest) != policy.HashFileSha256)) return false;
            if (file.Path == C2Path && (file.Kind != (int)SignatureFileKind.Ioc || Convert.ToHexStringLower(digest) != policy.C2FileSha256)) return false;
        }
        return files.Any(x => x.Path == "LICENSE") && files.Any(x => x.Path == "README.md") &&
            files.Any(x => x.Path == HashPath) && files.Any(x => x.Path == C2Path);
    }
}

/// <summary>Explicitly reviewed file hashes; changed source content needs a new review.</summary>
internal sealed record IocSourcePolicy(string HashFileSha256, string C2FileSha256);
