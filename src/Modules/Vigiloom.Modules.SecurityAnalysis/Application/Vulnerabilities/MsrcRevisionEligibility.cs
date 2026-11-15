using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Checks indexed revision integrity; it does not assert host applicability or complete CVE coverage.</summary>
internal static class MsrcRevisionEligibility
{
    internal static async Task<bool> HasProductIndexAsync(SecurityAnalysisDbContext db, Guid revisionId, CancellationToken token) =>
        !await (from document in db.MsrcDocuments.AsNoTracking()
            join artifact in db.MsrcArtifacts.AsNoTracking() on document.Digest equals artifact.Digest
            where document.RevisionId == revisionId && (artifact.IndexedProductCount < 0 ||
                db.MsrcProductIndexes.Count(x => x.Digest == artifact.Digest) != artifact.IndexedProductCount)
            select artifact.RowId).AnyAsync(token);

    internal static async Task<bool> IsPublishableAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token) =>
        await AssociationCountAsync(db, sourceId, revisionId, token) is not null;

    internal static async Task<int?> AssociationCountAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.Id == revisionId)
            .Select(x => new { x.Digest, x.Revoked, x.Normalized, x.MappingVersion, x.SchemaVersion, x.LicenseEvidence, x.RecordCount })
            .SingleOrDefaultAsync(token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.RecordCount <= 0 ||
            revision.MappingVersion != MsrcSnapshotStage.Mapping || revision.SchemaVersion != MsrcSnapshotStage.Schema ||
            revision.LicenseEvidence != MsrcRefresh.LicenseEvidence ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            await db.MsrcDocuments.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount) return null;
        if (await (from document in db.MsrcDocuments
            join artifact in db.MsrcArtifacts on document.Digest equals artifact.Digest
            where document.RevisionId == revisionId && (artifact.IndexedCveCount < 0 ||
                db.MsrcCveIndexes.Count(x => x.Digest == artifact.Digest) != artifact.IndexedCveCount)
            select artifact.RowId).AnyAsync(token)) return null;
        return await (from document in db.MsrcDocuments
            join artifact in db.MsrcArtifacts on document.Digest equals artifact.Digest
            where document.RevisionId == revisionId select (int?)artifact.IndexedCveCount).SumAsync(token) ?? 0;
    }
}
