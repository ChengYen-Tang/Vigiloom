using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

public sealed class AuditRetentionCleanup(IDbContextFactory<OperationsDbContext> contexts, AuditRetentionSettings settings)
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
            var ids = db.AuditEvents.Where(x => x.OccurredAt < cutoff).OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId)
                .Select(x => x.RowId).Take(BatchSize);
            var removed = await db.AuditEvents.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
            await tx.CommitAsync(token);
            total += removed;
            if (removed < BatchSize) return total;
        }
    }
}
