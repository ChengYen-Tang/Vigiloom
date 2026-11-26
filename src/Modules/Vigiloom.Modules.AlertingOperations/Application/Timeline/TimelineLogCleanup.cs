using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Timeline;

/// <summary>Removes local log summaries once their corresponding retained logs are deleted.</summary>
public sealed class TimelineLogCleanup(IDbContextFactory<OperationsDbContext> contexts)
{
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
