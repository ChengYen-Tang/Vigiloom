using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

public sealed class AuditRetentionCleanup(IDbContextFactory<OperationsDbContext> contexts, AuditRetentionSettings settings, IEnumerable<IAuditRetentionSource> sources,
    IEnumerable<IBackgroundHistorySource>? backgroundSources = null)
{
    private const int BatchSize = 500;

    public async Task<long> RunAsync(CancellationToken token)
    {
        await settings.EnsureAsync(token);
        long total = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await AuditRetentionSettings.LockAsync(db, token);
            var cutoff = (await AuditRetentionSettings.NowAsync(db, token)).AddDays(-policy.Days);
            var alertsRemoved = await AlertRetention.DeleteBatchAsync(db, cutoff, BatchSize, token);
            var jobsRemoved = await BackgroundHistoryRetention.DeleteBatchAsync(db, cutoff, BatchSize, token);
            var coveredDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { await BackgroundHistoryRetention.DatabaseAsync(db, token) };
            foreach (var source in backgroundSources ?? [])
            {
                var result = await source.DeleteBatchAsync(cutoff, BatchSize, coveredDatabases, token);
                coveredDatabases.Add(result.Database);
                jobsRemoved = checked(jobsRemoved + (int)result.Count);
            }
            var ids = db.AuditEvents.Where(x => x.OccurredAt < cutoff).OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId)
                .Select(x => x.RowId).Take(BatchSize);
            var removed = await db.AuditEvents.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var sourceRemoved = 0;
            foreach (var source in sources)
                sourceRemoved += await source.DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), cutoff, BatchSize, token);
            await tx.CommitAsync(token);
            total += removed + alertsRemoved + jobsRemoved + sourceRemoved;
            if (removed < BatchSize && alertsRemoved < BatchSize && jobsRemoved < BatchSize && sourceRemoved < BatchSize) return total;
        }
    }
}
