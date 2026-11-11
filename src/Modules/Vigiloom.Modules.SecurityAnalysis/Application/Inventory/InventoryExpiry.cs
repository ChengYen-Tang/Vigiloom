using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Closes abandoned staging generations without deleting current inventory or replay fences.</summary>
public sealed class InventoryExpiry(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromHours(24);
    private const int MaximumPerScan = 100;

    public async Task<int> RunAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await DatabaseTimeAsync(db, token);
        var expired = 0;
        long afterRow = 0;
        while (expired < MaximumPerScan)
        {
            var candidates = await db.InventorySnapshots.AsNoTracking()
                .Where(x => x.Status == InventorySnapshotStatus.Staging && x.StartedAt <= now - StagingLifetime && x.RowId > afterRow)
                .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.AgentId, x.Source, x.Generation })
                .Take(MaximumPerScan).ToArrayAsync(token);
            if (candidates.Length == 0) break;
            foreach (var candidate in candidates)
            {
                afterRow = candidate.RowId;
                token.ThrowIfCancellationRequested();
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                if (!await InventoryAgentLock.TryAcquireAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), candidate.AgentId, 0, token))
                    continue;
                // Re-read under the same lock and database clock used by receipt transactions.
                var currentTime = await DatabaseTimeAsync(db, token);
                var source = await db.InventorySources.SingleOrDefaultAsync(x => x.AgentId == candidate.AgentId &&
                    x.Source == candidate.Source && x.ActiveGeneration == candidate.Generation, token);
                if (source is null) continue;
                var snapshot = await db.InventorySnapshots.SingleOrDefaultAsync(x => x.AgentId == candidate.AgentId &&
                    x.Source == candidate.Source && x.Generation == candidate.Generation && x.Status == InventorySnapshotStatus.Staging, token);
                if (snapshot is null || snapshot.StartedAt > currentTime - StagingLifetime) continue;
                await InventoryStaging.CloseExpiredAsync(db, source, snapshot, currentTime, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                if (++expired == MaximumPerScan) break;
            }
        }
        return expired;
    }

    private static Task<DateTimeOffset> DatabaseTimeAsync(SecurityAnalysisDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
}
