using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application.Retention;

/// <summary>Applies the central audit retention policy to device operations.</summary>
public sealed class DeviceAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        return await db.DeviceAudits.LongCountAsync(x => x.OccurredAt < cutoff, token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var ids = db.DeviceAudits.Where(x => x.OccurredAt < cutoff).OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId)
            .Select(x => x.RowId).Take(batchSize);
        return await db.DeviceAudits.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
    }

    private static async Task<ResourcesDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var db = new ResourcesDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
