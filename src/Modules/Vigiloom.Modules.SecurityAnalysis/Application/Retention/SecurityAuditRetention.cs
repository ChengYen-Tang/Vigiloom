using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Applies the central audit policy to inventory and integrity configuration changes.</summary>
public sealed class SecurityAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        return await AuditRetentionQueries.CountAsync(db.InventorySettingAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.IntegrityScopeAudits, cutoff, token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var removed = await AuditRetentionQueries.DeleteBatchAsync(db.InventorySettingAudits, cutoff, batchSize, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.IntegrityScopeAudits, cutoff, batchSize - removed, token);
        return removed;
    }

    private static async Task<SecurityAnalysisDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
