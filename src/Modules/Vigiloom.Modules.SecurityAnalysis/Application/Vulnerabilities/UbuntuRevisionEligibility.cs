using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal static class UbuntuRevisionEligibility
{
    internal static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    internal static async Task<bool> IsPublishableAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, DateTimeOffset now, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.Id == revisionId)
            .Select(x => new { x.Digest, x.Revoked, x.Normalized, x.MappingVersion, x.SchemaVersion, x.LicenseEvidence, x.RecordCount })
            .SingleOrDefaultAsync(token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.RecordCount <= 0 ||
            revision.MappingVersion != UbuntuRevisionStage.MappingVersion || revision.SchemaVersion != UbuntuRevisionStage.SchemaVersion ||
            revision.LicenseEvidence != UbuntuRevisionStage.LicenseEvidence ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token) ||
            await db.UbuntuRevisionDocuments.CountAsync(x => x.RevisionId == revisionId, token) != revision.RecordCount) return false;

        var latestAllowed = now.Add(MaximumClockSkew);
        if (await (from member in db.UbuntuRevisionDocuments
            join document in db.UbuntuDocuments on new { member.Digest, member.MappingVersion, member.AdvisoryId }
                equals new { document.Digest, document.MappingVersion, document.AdvisoryId }
            where member.RevisionId == revisionId && (document.ModifiedAt > latestAllowed || document.WithdrawnAt > latestAllowed)
            select document.RowId).AnyAsync(token)) return false;

        return await PreservesPublishedWithdrawalsAsync(db, sourceId, revisionId, token);
    }

    internal static async Task<bool> PreservesPublishedWithdrawalsAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token) =>
        !await (from member in db.UbuntuRevisionDocuments
            join candidate in db.UbuntuDocuments on new { member.Digest, member.MappingVersion, member.AdvisoryId }
                equals new { candidate.Digest, candidate.MappingVersion, candidate.AdvisoryId }
            where member.RevisionId == revisionId && candidate.WithdrawnAt == null &&
                (from previousMember in db.UbuntuRevisionDocuments
                 join previous in db.UbuntuDocuments on new { previousMember.Digest, previousMember.MappingVersion, previousMember.AdvisoryId }
                    equals new { previous.Digest, previous.MappingVersion, previous.AdvisoryId }
                 where previousMember.AdvisoryId == member.AdvisoryId && previous.WithdrawnAt != null &&
                    (previous.ModifiedAt >= candidate.ModifiedAt || previous.WithdrawnAt >= candidate.ModifiedAt) &&
                    db.IntelligenceActivationEvents.Any(e => e.SourceId == sourceId && e.RevisionId == previousMember.RevisionId)
                 select previous.RowId).Any()
            select candidate.RowId).AnyAsync(token);
}
