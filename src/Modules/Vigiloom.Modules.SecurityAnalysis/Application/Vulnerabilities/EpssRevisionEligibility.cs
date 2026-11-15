using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Establishes that the retained EPSS snapshot is complete and has not been revoked.</summary>
internal static class EpssRevisionEligibility
{
    internal static async Task<bool> IsPublishableAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, DateTimeOffset now, int staleAfterSeconds, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.Id == revisionId)
            .Select(x => new { x.Digest, x.Artifact, x.Revoked, x.Normalized, x.MappingVersion, x.SchemaVersion, x.LicenseEvidence, x.RecordCount, x.ReleasedAt })
            .SingleOrDefaultAsync(token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.RecordCount <= 0 ||
            revision.MappingVersion != EpssSnapshotStage.Mapping || revision.SchemaVersion != EpssSnapshotStage.Schema ||
            revision.LicenseEvidence != EpssRefresh.LicenseEvidence || revision.ReleasedAt is null || revision.ReleasedAt > now || revision.ReleasedAt < now.AddSeconds(-staleAfterSeconds) ||
            revision.Artifact.Length is < 18 or > 64 * 1024 * 1024 ||
            !SHA256.HashData(revision.Artifact).AsSpan().SequenceEqual(revision.Digest) ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            await db.EpssScores.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount) return false;
        return true;
    }
}
