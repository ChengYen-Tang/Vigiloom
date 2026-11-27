using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

public sealed class LegacyOperationsRetention
{
    public async Task<LegacyOperationsRetentionCounts> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var audit = await db.AuditEvents.LongCountAsync(row => row.OccurredAt < cutoff, token);
        var (alerts, notifications) = await AlertRetention.CountAsync(db, cutoff, token);
        var jobs = await BackgroundHistoryRetention.CountAsync(db, cutoff, token);
        return new(audit, alerts, notifications, jobs);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var removed = await AlertRetention.DeleteBatchAsync(db, cutoff, batchSize, token);
        if (removed < batchSize) removed += await BackgroundHistoryRetention.DeleteBatchAsync(db, cutoff, batchSize - removed, token);
        if (removed < batchSize)
        {
            var ids = db.AuditEvents.Where(row => row.OccurredAt < cutoff).OrderBy(row => row.OccurredAt).ThenBy(row => row.RowId)
                .Select(row => row.RowId).Take(batchSize - removed);
            removed += await db.AuditEvents.Where(row => ids.Contains(row.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static async Task<OperationsDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
