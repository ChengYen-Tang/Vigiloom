using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

/// <summary>Closes abandoned scans and releases retired baselines while preserving generation fences and change records.</summary>
public sealed class IntegrityExpiry(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromHours(24);
    private const int BatchSize = 50;

    public async Task<int> RunAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await DatabaseTimeAsync(db, token);
        var count = 0;
        var expiredCount = 0;
        long afterScan = 0;
        while (expiredCount < BatchSize)
        {
        var expired = await (from scan in db.IntegrityScans.AsNoTracking()
            join scope in db.IntegrityScopes.AsNoTracking() on scan.ScopeId equals scope.Id
            where scan.RowId > afterScan && scan.Status == IntegrityScanStatus.Staging &&
                (scan.StartedAt <= now - StagingLifetime || scope.RetiredAt != null || scope.Revision != scan.ScopeRevision)
            orderby scan.RowId
            select new { scan.RowId, scan.AgentId, scan.ScopeId, scan.Generation }).Take(BatchSize).ToArrayAsync(token);
        if (expired.Length == 0) break;
        foreach (var candidate in expired)
        {
            afterScan = candidate.RowId;
            token.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await AgentSnapshotLock.TryAcquireAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), "Integrity", candidate.AgentId, 0, token)) continue;
            var currentTime = await DatabaseTimeAsync(db, token);
            var state = await db.IntegrityScopeStates.SingleOrDefaultAsync(x => x.AgentId == candidate.AgentId && x.ScopeId == candidate.ScopeId &&
                x.ActiveGeneration == candidate.Generation, token);
            var scan = await db.IntegrityScans.SingleOrDefaultAsync(x => x.AgentId == candidate.AgentId && x.ScopeId == candidate.ScopeId &&
                x.Generation == candidate.Generation && x.Status == IntegrityScanStatus.Staging, token);
            var scope = await db.IntegrityScopes.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityScopes] WITH (HOLDLOCK) WHERE [Id] = {candidate.ScopeId}")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (state is null || scan is null || scope is null ||
                scan.StartedAt > currentTime - StagingLifetime && scope.RetiredAt is null && scope.Revision == scan.ScopeRevision) continue;
            await IntegrityStageWriter.CloseAsync(db, state, scan, IntegrityScanStatus.Expired, currentTime, token);
            await transaction.CommitAsync(token);
            count++;
            if (++expiredCount == BatchSize) break;
        }
        }

        var retiredCount = 0;
        long afterState = 0;
        while (retiredCount < BatchSize)
        {
        var retired = await (from state in db.IntegrityScopeStates.AsNoTracking()
            join scope in db.IntegrityScopes.AsNoTracking() on state.ScopeId equals scope.Id
            where state.RowId > afterState && scope.RetiredAt != null && (state.CurrentGeneration != null || state.ActiveGeneration != null)
            orderby state.RowId
            select new { state.RowId, state.AgentId, state.ScopeId }).Take(BatchSize).ToArrayAsync(token);
        if (retired.Length == 0) break;
        foreach (var candidate in retired)
        {
            afterState = candidate.RowId;
            token.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await AgentSnapshotLock.TryAcquireAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), "Integrity", candidate.AgentId, 0, token)) continue;
            var scope = await db.IntegrityScopes.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityScopes] WITH (HOLDLOCK) WHERE [Id] = {candidate.ScopeId}")
                .AsNoTracking().SingleOrDefaultAsync(token);
            var state = await db.IntegrityScopeStates.SingleOrDefaultAsync(x => x.AgentId == candidate.AgentId && x.ScopeId == candidate.ScopeId, token);
            if (scope?.RetiredAt is null || state is null) continue;
            if (state.ActiveGeneration is long active)
            {
                var pending = await db.IntegrityScans.SingleAsync(x => x.AgentId == candidate.AgentId && x.ScopeId == candidate.ScopeId && x.Generation == active, token);
                await IntegrityStageWriter.CloseAsync(db, state, pending, IntegrityScanStatus.Expired, await DatabaseTimeAsync(db, token), token);
            }
            if (state.CurrentGeneration is long generation)
            {
                var scan = await db.IntegrityScans.SingleAsync(x => x.AgentId == candidate.AgentId && x.ScopeId == candidate.ScopeId && x.Generation == generation, token);
                await IntegrityStageWriter.DeleteChunksAsync(db, scan, token);
                db.IntegrityScans.Remove(scan);
                state.CurrentGeneration = null;
                state.LastCompleteAt = null;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            count++;
            if (++retiredCount == BatchSize) break;
        }
        }
        return count;
    }

    private static Task<DateTimeOffset> DatabaseTimeAsync(SecurityAnalysisDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
}
