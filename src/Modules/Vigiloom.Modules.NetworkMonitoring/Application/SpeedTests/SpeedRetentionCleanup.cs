using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

/// <summary>Removes bounded batches of expired, terminal speed tests under the scheduling lock.</summary>
public sealed class SpeedRetentionCleanup(IDbContextFactory<NetworkDbContext> contexts)
{
    public const int BatchSize = 500;
    public const int MaxBatchesPerRun = 10;

    public async Task<int> RunAsync(CancellationToken token)
    {
        await SpeedDatabase.EnsureAsync(contexts, token);
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var settings = await SpeedDatabase.LockAsync(db, token);
            var now = await SpeedDatabase.NowAsync(db, token);
            var cutoff = now.AddDays(-settings.RetentionDays);
            var protectedSince = now.AddHours(-24);
            var candidates = db.SpeedRuns.Where(x => x.RequestedAt < cutoff &&
                    x.State != SpeedTestState.Queued && x.State != SpeedTestState.Running &&
                    (x.StartedAt ?? x.RequestedAt) <= protectedSince)
                .OrderBy(x => x.RequestedAt).ThenBy(x => x.RowId).Select(x => x.Id).Take(BatchSize);
            var removed = await db.SpeedRuns.Where(x => candidates.Contains(x.Id)).ExecuteDeleteAsync(token);
            if (removed > 0)
            {
                settings.RetentionDeletedTotal = checked(settings.RetentionDeletedTotal + removed);
                settings.RetentionLastBatchCount = removed;
                settings.RetentionLastBatchAt = now;
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            total += removed;
            if (removed < BatchSize) break;
        }
        return total;
    }
}
