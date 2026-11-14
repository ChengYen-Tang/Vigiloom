using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

/// <summary>Rechecks retained advisory revision provenance whenever it becomes a base or is activated.</summary>
internal static class AdvisoryRevisionEligibility
{
    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.MappingVersion != AdvisoryRevisionBuilder.MappingVersion ||
            revision.SchemaVersion != AdvisoryRevisionBuilder.SchemaVersion || revision.LicenseEvidence != AdvisoryRevisionBuilder.LicenseEvidence ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            !await db.AdvisoryImports.AnyAsync(x => x.SourceId == sourceId && x.ResultRevisionId == revisionId && x.Phase == 4 && x.ReconciliationPhase == 3, token) ||
            await db.AdvisoryRevisionEntries.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount) return false;

        var invalidOrigin = await (from member in db.AdvisoryRevisionEntries
            join entry in db.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
            join origin in db.AdvisoryImports on entry.ImportId equals origin.Id
            where member.RevisionId == revisionId &&
                (origin.SourceId != sourceId || origin.Phase != 4 || origin.ReconciliationPhase != 3 || origin.ResultRevisionId == null || entry.Reconciliation)
            select member.RowId).AnyAsync(token);
        if (invalidOrigin) return false;
        return !await (from member in db.AdvisoryRevisionEntries
            join entry in db.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
            join origin in db.AdvisoryImports on entry.ImportId equals origin.Id
            join provenance in db.IntelligenceRevisions on origin.ResultRevisionId equals (Guid?)provenance.Id
            where member.RevisionId == revisionId &&
                (provenance.SourceId != sourceId || provenance.Revoked || !provenance.Normalized ||
                 provenance.MappingVersion != AdvisoryRevisionBuilder.MappingVersion || provenance.SchemaVersion != AdvisoryRevisionBuilder.SchemaVersion ||
                 provenance.LicenseEvidence != AdvisoryRevisionBuilder.LicenseEvidence || db.IntelligenceRevisions.Any(r =>
                     r.SourceId == sourceId && r.Digest == provenance.Digest && r.Revoked))
            select member.RowId).AnyAsync(token);
    }
}
