using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal static class DebianRevisionEligibility
{
    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.Id == revisionId)
            .Select(x => new { x.Digest, x.Revoked, x.Normalized, x.MappingVersion, x.SchemaVersion, x.LicenseEvidence, x.RecordCount })
            .SingleOrDefaultAsync(token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.RecordCount <= 0 ||
            revision.MappingVersion != DebianRevisionStage.MappingVersion || revision.SchemaVersion != DebianRevisionStage.SchemaVersion ||
            revision.LicenseEvidence != DebianRefresh.LicenseEvidence ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token)) return false;
        return await db.DebianAdvisories.CountAsync(x => x.RevisionId == revisionId, token) == revision.RecordCount;
    }
}
