using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;

namespace Vigiloom.Infrastructure.Scheduling.Retention;

/// <summary>Removes completed Dashboard operation records while keeping unfinished operations.</summary>
public sealed class DashboardAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        return await db.DashboardAudit.LongCountAsync(x => x.OccurredAt < cutoff && x.Result != "started", token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var ids = db.DashboardAudit.Where(x => x.OccurredAt < cutoff && x.Result != "started")
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize);
        return await db.DashboardAudit.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
    }

    private static async Task<SchedulingDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        var db = new SchedulingDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
