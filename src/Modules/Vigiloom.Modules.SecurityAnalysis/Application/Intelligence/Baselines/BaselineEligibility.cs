using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

internal static class BaselineEligibility
{
    internal static bool IsEligible(BaselineDefinition definition, IntelligenceRevision revision) =>
        !revision.Revoked && revision.Normalized && revision.RecordCount == 1 && revision.UpstreamVersion == definition.Commit &&
        revision.SchemaVersion == BaselineDefinitions.Schema && revision.MappingVersion == BaselineDefinitions.Mapping &&
        revision.LicenseEvidence == definition.LicenseEvidence &&
        revision.Artifact.AsSpan().SequenceEqual(definition.Artifact) && SHA256.HashData(revision.Artifact).AsSpan().SequenceEqual(revision.Digest);

    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, string key, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var definition = BaselineDefinitions.Find(key);
        if (definition is null) return false;
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        return revision is not null && IsEligible(definition, revision) &&
            !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token);
    }
}
