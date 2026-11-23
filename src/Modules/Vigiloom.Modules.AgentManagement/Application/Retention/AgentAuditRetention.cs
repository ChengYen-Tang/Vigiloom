using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.Retention;

/// <summary>Applies the central audit policy to Agent and Agent configuration changes.</summary>
public sealed class AgentAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        return await AuditRetentionQueries.CountAsync(db.Audits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.HostLogSourceAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.SettingAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.QueueCapacityAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.MetricSettingAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.HostMetricThresholdAudits, cutoff, token) +
            await db.YaraScans.LongCountAsync(x => x.CompletedAt != null && x.CompletedAt < cutoff, token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var removed = await AuditRetentionQueries.DeleteBatchAsync(db.Audits, cutoff, batchSize, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.HostLogSourceAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.SettingAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.QueueCapacityAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.MetricSettingAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.HostMetricThresholdAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize)
        {
            var scans = db.YaraScans.Where(x => x.CompletedAt != null && x.CompletedAt < cutoff)
                .OrderBy(x => x.CompletedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.YaraScans.Where(x => scans.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static async Task<AgentManagementDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
        var db = new AgentManagementDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
