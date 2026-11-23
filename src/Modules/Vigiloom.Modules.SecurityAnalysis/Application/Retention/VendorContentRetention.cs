using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Reclaims unreferenced vendor documents while preserving published withdrawal evidence.</summary>
internal static class VendorContentRetention
{
    private static IQueryable<Domain.Intelligence.UbuntuRevisionDocument> RemovableUbuntu(SecurityAnalysisDbContext db) =>
        from member in db.UbuntuRevisionDocuments
        join document in db.UbuntuDocuments on new { member.Digest, member.MappingVersion, member.AdvisoryId }
            equals new { document.Digest, document.MappingVersion, document.AdvisoryId }
        where document.WithdrawnAt == null
        select member;

    private static IQueryable<Domain.Intelligence.IntelligenceRevision> Candidates(SecurityAnalysisDbContext db, DateTimeOffset cutoff, Guid[] previous) =>
        from revision in IntelligenceContentRetention.Unreferenced(db, cutoff, previous)
        join source in db.IntelligenceSources on revision.SourceId equals source.Id
        where (source.Key == "ubuntu-osv" && RemovableUbuntu(db).Any(x => x.RevisionId == revision.Id)) ||
              (source.Key == "msrc-cvrf" && db.MsrcDocuments.Any(x => x.RevisionId == revision.Id))
        select revision;

    public static async Task<long> CountAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, CancellationToken token)
    {
        var previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token);
        var revisions = Candidates(db, cutoff, previous);
        return await revisions.LongCountAsync(x => x.ContentPurgedAt == null, token) +
            await RemovableUbuntu(db).LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token) +
            await db.MsrcDocuments.LongCountAsync(x => revisions.Any(r => r.Id == x.RevisionId), token) +
            await db.UbuntuScopes.LongCountAsync(x => !db.UbuntuRevisionDocuments.Any(m => m.Digest == x.Digest && m.MappingVersion == x.MappingVersion &&
                !RemovableUbuntu(db).Any(removable => removable.RevisionId == m.RevisionId && removable.AdvisoryId == m.AdvisoryId && revisions.Any(r => r.Id == removable.RevisionId))), token) +
            await db.UbuntuDocuments.LongCountAsync(x => !db.UbuntuRevisionDocuments.Any(m => m.Digest == x.Digest && m.MappingVersion == x.MappingVersion &&
                !RemovableUbuntu(db).Any(removable => removable.RevisionId == m.RevisionId && removable.AdvisoryId == m.AdvisoryId && revisions.Any(r => r.Id == removable.RevisionId))), token) +
            await db.MsrcCveIndexes.LongCountAsync(x => !db.MsrcDocuments.Any(m => m.Digest == x.Digest && !revisions.Any(r => r.Id == m.RevisionId)), token) +
            await db.MsrcProductIndexes.LongCountAsync(x => !db.MsrcDocuments.Any(m => m.Digest == x.Digest && !revisions.Any(r => r.Id == m.RevisionId)), token) +
            await db.MsrcArtifacts.LongCountAsync(x => !db.MsrcDocuments.Any(m => m.Digest == x.Digest && !revisions.Any(r => r.Id == m.RevisionId)), token);
    }

    public static async Task<int> DeleteBatchAsync(SecurityAnalysisDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        var previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token);
        var candidate = await Candidates(db, cutoff, previous).OrderBy(x => x.RetrievedAt).ThenBy(x => x.RowId)
            .Select(x => new { x.Id, x.SourceId }).FirstOrDefaultAsync(token);
        if (candidate is not null)
        {
            if (!await TryLockAsync(db, candidate.SourceId, token)) return 0;
            previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token, candidate.SourceId);
            var revision = await Candidates(db, cutoff, previous).SingleOrDefaultAsync(x => x.Id == candidate.Id, token);
            if (revision is null) return 0;
            var removed = 0;
            if (revision.ContentPurgedAt is null)
            {
                revision.ContentPurgedAt = DateTimeOffset.UtcNow; revision.Normalized = false; revision.Artifact = [];
                await db.SaveChangesAsync(token); removed++;
            }
            if (removed < batchSize)
            {
                var ids = RemovableUbuntu(db).Where(x => x.RevisionId == revision.Id).OrderBy(x => x.AdvisoryId)
                    .Select(x => x.AdvisoryId).Take(batchSize - removed);
                removed += await db.UbuntuRevisionDocuments.Where(x => x.RevisionId == revision.Id && ids.Contains(x.AdvisoryId)).ExecuteDeleteAsync(token);
            }
            if (removed < batchSize)
            {
                var ids = db.MsrcDocuments.Where(x => x.RevisionId == revision.Id).OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
                removed += await db.MsrcDocuments.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
            }
            return removed;
        }
        foreach (var key in new[] { "ubuntu-osv", "msrc-cvrf" })
        {
            var sourceId = await db.IntelligenceSources.Where(x => x.Key == key).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            if (sourceId is null || !await TryLockAsync(db, sourceId.Value, token)) continue;
            var removed = key == "ubuntu-osv" ? await DeleteUbuntuOrphanAsync(db, token) : await DeleteMsrcOrphanAsync(db, token);
            if (removed > 0) return removed;
        }
        return 0;
    }

    private static async Task<bool> TryLockAsync(SecurityAnalysisDbContext db, Guid sourceId, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("A transaction is required.");
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.DbType = DbType.String; parameter.Size = 255; parameter.Value = $"Vigiloom.Intelligence.Refresh.{sourceId:D}"; command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return false;
        return await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").AsNoTracking().AnyAsync(token);
    }

    private static async Task<int> DeleteUbuntuOrphanAsync(SecurityAnalysisDbContext db, CancellationToken token)
    {
        var document = await db.UbuntuDocuments.AsNoTracking().Where(x => !db.UbuntuRevisionDocuments.Any(m => m.Digest == x.Digest && m.MappingVersion == x.MappingVersion))
            .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.Digest, x.MappingVersion }).FirstOrDefaultAsync(token);
        if (document is null) return 0;
        // A writer caches document digests; scopes and their document must disappear together.
        var removed = await db.UbuntuScopes.Where(x => x.Digest == document.Digest && x.MappingVersion == document.MappingVersion).ExecuteDeleteAsync(token);
        return removed + await db.UbuntuDocuments.Where(x => x.RowId == document.RowId).ExecuteDeleteAsync(token);
    }

    private static async Task<int> DeleteMsrcOrphanAsync(SecurityAnalysisDbContext db, CancellationToken token)
    {
        var artifact = await db.MsrcArtifacts.AsNoTracking().Where(x => !db.MsrcDocuments.Any(m => m.Digest == x.Digest))
            .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.Digest }).FirstOrDefaultAsync(token);
        if (artifact is null) return 0;
        // Keep each indexed artifact consistent between cleanup batches and refreshes.
        var removed = await db.MsrcCveIndexes.Where(x => x.Digest == artifact.Digest).ExecuteDeleteAsync(token);
        removed += await db.MsrcProductIndexes.Where(x => x.Digest == artifact.Digest).ExecuteDeleteAsync(token);
        return removed + await db.MsrcArtifacts.Where(x => x.RowId == artifact.RowId).ExecuteDeleteAsync(token);
    }
}
