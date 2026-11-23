using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Reclaims unused advisory snapshots without losing shared provenance or published withdrawals.</summary>
internal static class AdvisoryContentRetention
{
    private static IQueryable<AdvisoryRevisionEntry> OrdinaryMembers(SecurityAnalysisDbContext db) =>
        from member in db.AdvisoryRevisionEntries
        join entry in db.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
        where entry.WithdrawnAt == null
        select member;

    private static IQueryable<IntelligenceRevision> Candidates(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] previous) =>
        from revision in IntelligenceContentRetention.Unreferenced(db, cutoff, previous)
        join source in db.IntelligenceSources on revision.SourceId equals source.Id
        where source.Key == "github-reviewed" && OrdinaryMembers(db).Any(member => member.RevisionId == revision.Id) &&
            !db.AdvisoryImports.Any(import => import.SourceId == source.Id && import.Phase < 4 && (import.BaseRevisionId == revision.Id || import.ResultRevisionId == revision.Id)) &&
            !(from member in db.AdvisoryRevisionEntries
              join entry in db.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
              join import in db.AdvisoryImports on entry.ImportId equals import.Id
              where import.ResultRevisionId == revision.Id && member.RevisionId != revision.Id
              select member.RowId).Any()
        select revision;

    internal static IQueryable<AdvisoryImportEntry> ReclaimableEntries(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] previous)
    {
        var revisions = Candidates(db, cutoff, previous);
        var removable = OrdinaryMembers(db).Where(member => revisions.Any(revision => revision.Id == member.RevisionId));
        return db.AdvisoryImportEntries.Where(entry => db.AdvisoryImports.Any(import => import.Id == entry.ImportId && (import.Phase == 4 || import.Phase == 5) && import.StartedAt < cutoff) &&
            !db.AdvisoryRevisionEntries.Any(reference => reference.EntryRowId == entry.RowId && !removable.Any(member => member.RowId == reference.RowId)));
    }

    internal static IQueryable<AdvisoryImportPage> ReclaimablePages(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] previous)
    {
        var entries = ReclaimableEntries(db, cutoff, previous);
        return db.AdvisoryImportPages.Where(page => db.AdvisoryImports.Any(import => import.Id == page.ImportId && (import.Phase == 4 || import.Phase == 5) && import.StartedAt < cutoff) &&
            !db.AdvisoryImportEntries.Any(entry => entry.PageRowId == page.RowId && !entries.Any(reclaimable => reclaimable.RowId == entry.RowId)));
    }

    public static async Task<long> CountAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] previous, CancellationToken token)
    {
        var revisions = Candidates(db, cutoff, previous);
        return await revisions.LongCountAsync(x => x.ContentPurgedAt == null, token) +
            await OrdinaryMembers(db).LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token);
    }

    public static async Task<int> DeleteBatchAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        var previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token);
        var candidate = await Candidates(db, cutoff, previous).OrderBy(x => x.RetrievedAt).ThenBy(x => x.RowId).Select(x => new { x.Id, x.SourceId }).FirstOrDefaultAsync(token);
        if (candidate is null) return 0;
        await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidate.SourceId}")
            .AsNoTracking().SingleAsync(token);
        previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token, candidate.SourceId);
        var revision = await Candidates(db, cutoff, previous).SingleOrDefaultAsync(x => x.Id == candidate.Id, token);
        if (revision is null) return 0;
        var removed = 0;
        if (revision.ContentPurgedAt is null)
        {
            revision.ContentPurgedAt = DateTimeOffset.UtcNow; revision.Normalized = false;
            await db.SaveChangesAsync(token); removed++;
        }
        if (removed < batchSize)
        {
            var ids = OrdinaryMembers(db).Where(x => x.RevisionId == revision.Id).OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.AdvisoryRevisionEntries.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }
}
