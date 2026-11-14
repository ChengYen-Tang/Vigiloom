using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

/// <summary>Checks the retained source subset, independently of detector support.</summary>
internal static class SigmaRevisionEligibility
{
    internal static async Task<int?> IncludedCountAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        if (revision is null || revision.Revoked || !revision.Normalized ||
            revision.SchemaVersion != SigmaRefresh.SchemaVersion || revision.MappingVersion != SigmaRefresh.MappingVersion ||
            revision.LicenseEvidence != SigmaRefresh.LicenseEvidence || revision.UpstreamVersion.Length != 40 ||
            !revision.UpstreamVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(revision.Artifact), revision.Digest) ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token)) return null;

        var stored = await db.SigmaManifests.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revisionId, token);
        if (stored is null || stored.PolicyVersion != SigmaManifestBuilder.PolicyVersion || stored.IncludedCount <= 0 ||
            (long)stored.IncludedCount + stored.ExcludedCount != revision.RecordCount) return null;
        SigmaManifest manifest;
        try { manifest = await SigmaManifestBuilder.BuildAsync(db, revisionId, revision.UpstreamVersion, token); }
        catch (InvalidDataException) { return null; }
        return manifest.IncludedCount == stored.IncludedCount && manifest.ExcludedCount == stored.ExcludedCount &&
               CryptographicOperations.FixedTimeEquals(manifest.Digest, stored.Digest) ? stored.IncludedCount : null;
    }
}
