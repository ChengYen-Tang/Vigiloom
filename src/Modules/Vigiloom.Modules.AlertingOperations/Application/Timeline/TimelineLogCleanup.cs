using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Timeline;

/// <summary>Removes local log summaries once their corresponding retained logs are deleted.</summary>
public sealed class TimelineLogCleanup(IDbContextFactory<OperationsDbContext> contexts)
{
    public async Task<int> RunHealthBatchAsync(int retentionDays, CancellationToken token)
    {
        if (retentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (500) FROM [operations].[TimelineRecords] WHERE [Source] = 'service-health' AND [Time] < DATEADD(day, {-retentionDays}, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
    }
    public async Task<long> RunAsync(CancellationToken token)
    {
        const int batchSize = 500;
        long total = 0;
        int removed;
        do
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            removed = await db.Database.ExecuteSqlRawAsync("""
                DELETE TOP (500) timeline
                FROM operations.TimelineRecords AS timeline
                WHERE timeline.LocalLogRowId IS NOT NULL AND
                    ((timeline.Source = 'host-log' AND NOT EXISTS (SELECT 1 FROM logs.HostLogs AS log WHERE log.RowId = timeline.LocalLogRowId)) OR
                     (timeline.Source = 'service-log' AND NOT EXISTS (SELECT 1 FROM logs.LogRecords AS log WHERE log.RowId = timeline.LocalLogRowId)))
                """, token);
            total += removed;
        } while (removed == batchSize);
        return total;
    }
}
