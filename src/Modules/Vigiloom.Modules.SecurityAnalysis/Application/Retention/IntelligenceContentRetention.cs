using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Removes unused intelligence contents while keeping revision and activation metadata.</summary>
internal static class IntelligenceContentRetention
{
    private static IQueryable<IntelligenceRevision> Candidates(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] protectedPrevious) =>
        from revision in db.IntelligenceRevisions
        join source in db.IntelligenceSources on revision.SourceId equals source.Id
        join active in db.IntelligenceActivations on source.Id equals active.SourceId
        where (source.Key == "sigmahq-rules" || source.Key == "signature-base" || source.Key == "cisa-kev") &&
            revision.RetrievedAt < cutoff && !revision.Revoked && (revision.Normalized || revision.ContentPurgedAt != null) &&
            (revision.ContentPurgedAt == null || db.SigmaRules.Any(x => x.RevisionId == revision.Id) || db.SignatureFiles.Any(x => x.RevisionId == revision.Id) || db.KevRecords.Any(x => x.RevisionId == revision.Id)) &&
            revision.Id != active.RevisionId && revision.Id != source.LastObservedRevisionId &&
            !protectedPrevious.Contains(revision.Id) &&
            !db.IntelligenceCaches.Any(x => x.SourceId == source.Id && x.RevisionId == revision.Id) &&
            !db.OfflineImports.Any(x => x.RevisionId == revision.Id) &&
            !db.SigmaRuleTests.Any(x => x.RevisionId == revision.Id) && !db.IocDetections.Any(x => x.RevisionId == revision.Id) &&
            !db.BaselineObservations.Any(x => x.RevisionId == revision.Id) &&
            !db.VulnerabilityFindings.Any(x => x.LastRevisionId == revision.Id) && !db.VulnerabilityEvaluations.Any(x => x.RevisionId == revision.Id) &&
            !db.VulnerabilityCoverage.Any(x => x.RevisionId == revision.Id) && !db.VulnerabilityRuns.Any(x => x.RevisionId == revision.Id) &&
            revision.Id != db.IntelligenceRevisions.Where(x => x.SourceId == source.Id && x.ContentPurgedAt == null)
                .OrderByDescending(x => x.RetrievedAt).ThenByDescending(x => x.RowId).Select(x => (Guid?)x.Id).FirstOrDefault()
        select revision;

    public static async Task<long> CountAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, CancellationToken token)
    {
        var protectedPrevious = await ProtectedPreviousAsync(db, token);
        var revisions = Candidates(db, cutoff, protectedPrevious);
        return await revisions.LongCountAsync(x => x.ContentPurgedAt == null, token) +
            await db.SigmaRules.LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token) +
            await db.KevRecords.LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token) +
            await db.SignatureFiles.LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token) +
            await db.IocEntries.LongCountAsync(x => db.SignatureFiles.Any(f => f.RowId == x.SignatureFileRowId && revisions.Any(r => r.Id == f.RevisionId)), token);
    }

    public static async Task<int> DeleteBatchAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        var protectedPrevious = await ProtectedPreviousAsync(db, token);
        var candidate = await Candidates(db, cutoff, protectedPrevious).OrderBy(x => x.RetrievedAt).ThenBy(x => x.RowId)
            .Select(x => new { x.Id, x.SourceId }).FirstOrDefaultAsync(token);
        if (candidate is null) return 0;
        await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidate.SourceId}")
            .AsNoTracking().SingleAsync(token);
        protectedPrevious = await ProtectedPreviousAsync(db, token);
        var revision = await Candidates(db, cutoff, protectedPrevious).SingleOrDefaultAsync(x => x.Id == candidate.Id, token);
        if (revision is null) return 0;
        var removed = 0;
        if (revision.ContentPurgedAt is null)
        {
            revision.ContentPurgedAt = DateTimeOffset.UtcNow;
            revision.Normalized = false;
            if (await db.IntelligenceSources.AnyAsync(x => x.Id == revision.SourceId && x.Key == "cisa-kev", token)) revision.Artifact = [];
            await db.SaveChangesAsync(token);
            removed++;
        }
        if (removed < batchSize)
        {
            var ids = db.IocEntries.Where(x => db.SignatureFiles.Any(f => f.RowId == x.SignatureFileRowId && f.RevisionId == revision.Id))
                .OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.IocEntries.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var ids = db.SignatureFiles.Where(x => x.RevisionId == revision.Id && !db.IocEntries.Any(i => i.SignatureFileRowId == x.RowId))
                .OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.SignatureFiles.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var ids = db.SigmaRules.Where(x => x.RevisionId == revision.Id).OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.SigmaRules.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var ids = db.KevRecords.Where(x => x.RevisionId == revision.Id).OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.KevRecords.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static async Task<Guid[]> ProtectedPreviousAsync(SecurityAnalysisDbContext db, CancellationToken token)
    {
        var sources = await (from source in db.IntelligenceSources.AsNoTracking()
            join active in db.IntelligenceActivations.AsNoTracking() on source.Id equals active.SourceId
            where source.Key == "sigmahq-rules" || source.Key == "signature-base" || source.Key == "cisa-kev"
            select new { Source = source, active.RevisionId }).ToArrayAsync(token);
        var result = new List<Guid>(sources.Length);
        foreach (var source in sources)
        {
            var versions = await (from activation in db.IntelligenceActivationEvents.AsNoTracking()
                join revision in db.IntelligenceRevisions.AsNoTracking() on activation.RevisionId equals revision.Id
                where activation.SourceId == source.Source.Id && revision.Id != source.RevisionId && !revision.Revoked && revision.Normalized && revision.ContentPurgedAt == null
                orderby activation.OccurredAt descending, activation.RowId descending
                select revision.Id).ToArrayAsync(token);
            foreach (var revisionId in versions.Distinct())
            {
                var usable = source.Source.Key switch
                {
                    "sigmahq-rules" => await SigmaRevisionEligibility.IncludedCountAsync(db, source.Source.Id, revisionId, token) is not null,
                    "signature-base" => await IocSourceEligibility.IsEligibleAsync(db, source.Source.Id, revisionId, token),
                    _ => await KevUsableAsync(db, source.Source, revisionId, token)
                };
                if (!usable) continue;
                result.Add(revisionId);
                break;
            }
        }
        return result.ToArray();
    }

    private static async Task<bool> KevUsableAsync(SecurityAnalysisDbContext db, IntelligenceSource source, Guid revisionId, CancellationToken token)
    {
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId, token);
        var offline = revision.MappingVersion == KevUpdate.OfflineMappingVersion;
        return revision.SchemaVersion == KevCatalogValidator.SchemaRevision &&
            (offline ? revision.LicenseEvidence == KevUpdate.OfflineLicenseEvidence : revision.MappingVersion == KevUpdate.MappingVersion && revision.LicenseEvidence == KevUpdate.LicenseEvidence) &&
            !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == source.Id && x.Digest == revision.Digest && x.Revoked, token);
    }
}
