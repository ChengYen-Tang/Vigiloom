using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Retention;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AgentManagement.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;

namespace Vigiloom.Web.Diagnostics;

internal sealed class LegacyPlatformAuditRetention(IDbContextFactory<SchedulingDbContext> contexts,
    DashboardAuditRetention dashboard, AgentAuditRetention agent, SecurityAuditRetention security)
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
        await transaction.CommitAsync(token);
        return removed;
    }
}
