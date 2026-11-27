using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Retention;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AgentManagement.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Application.Retention;

namespace Vigiloom.Web.Diagnostics;

internal sealed class LegacyPlatformAuditRetention(IDbContextFactory<SchedulingDbContext> contexts,
    DashboardAuditRetention dashboard, AgentAuditRetention agent, SecurityAuditRetention security, LegacyOperationsRetention operations)
{
    public async Task<long> CountAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        long count = 0;
        foreach (IAuditRetentionSource source in new IAuditRetentionSource[] { dashboard, agent, security })
            count += await source.CountAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, token);
        return count;
    }

    public async Task<LegacyOperationsRetentionCounts> CountOperationsAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await IsExcludedAsync(db, request.ExcludedOperationsDatabase, token)) return new(0, 0, 0, 0);
        return await operations.CountAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, token);
    }

    public async Task<int> DeleteAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var removed = 0;
        foreach (IAuditRetentionSource source in new IAuditRetentionSource[] { dashboard, agent, security })
        {
            if (removed >= request.BatchSize) break;
            removed += await source.DeleteBatchAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, request.BatchSize - removed, token);
        }
        if (removed < request.BatchSize && !await IsExcludedAsync(db, request.ExcludedOperationsDatabase, token))
            removed += await operations.DeleteBatchAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, request.BatchSize - removed, token);
        await transaction.CommitAsync(token);
        return removed;
    }

    private static async Task<bool> IsExcludedAsync(SchedulingDbContext db, string? excluded, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(excluded)) return false;
        var database = await db.Database.SqlQueryRaw<string>("SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), '/', DB_NAME()) AS [Value]").SingleAsync(token);
        return string.Equals(database, excluded, StringComparison.OrdinalIgnoreCase);
    }
}
