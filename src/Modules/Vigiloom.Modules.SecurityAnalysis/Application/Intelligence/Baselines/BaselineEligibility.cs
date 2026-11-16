using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

internal static class BaselineEligibility
{
    internal const string CustomKeyPrefix = BaselineSources.LocalPrefix;
    internal const string CustomMapping = "baseline-local-v1";

    internal static bool IsCustomKey(string key, Guid sourceId) =>
        key == $"{CustomKeyPrefix}{sourceId:D}";

    internal static BaselineCheck? ReadCustom(IntelligenceRevision revision, Guid sourceId)
    {
        if (revision.SourceId != sourceId || revision.Revoked || !revision.Normalized || revision.RecordCount != 1 ||
            revision.SchemaVersion != BaselineDefinitions.Schema || revision.MappingVersion != CustomMapping ||
            !SHA256.HashData(revision.Artifact).AsSpan().SequenceEqual(revision.Digest)) return null;
        try
        {
            var check = JsonSerializer.Deserialize<BaselineCheck>(revision.Artifact);
            return CustomBaselineCheck.IsValid(check) && check!.Id == $"{CustomBaselineCheck.Prefix}{sourceId:D}" ? check : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool IsEligible(BaselineDefinition definition, IntelligenceRevision revision) =>
        !revision.Revoked && revision.Normalized && revision.RecordCount == 1 && revision.UpstreamVersion == definition.Commit &&
        revision.SchemaVersion == BaselineDefinitions.Schema && revision.MappingVersion == BaselineDefinitions.Mapping &&
        revision.LicenseEvidence == definition.LicenseEvidence &&
        revision.Artifact.AsSpan().SequenceEqual(definition.Artifact) && SHA256.HashData(revision.Artifact).AsSpan().SequenceEqual(revision.Digest);

    internal static async Task<bool> IsEligibleAsync(SecurityAnalysisDbContext db, string key, Guid sourceId, Guid revisionId, CancellationToken token)
    {
        var definition = BaselineDefinitions.Find(key);
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        return revision is not null && (definition is not null ? IsEligible(definition, revision) : IsCustomKey(key, sourceId) && ReadCustom(revision, sourceId) is not null) &&
            !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token);
    }
}
