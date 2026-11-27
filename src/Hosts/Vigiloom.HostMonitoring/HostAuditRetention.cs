using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.HostMonitoring;

internal sealed class HostAuditRetention(IDbContextFactory<HostMonitoringDbContext> contexts)
{
    internal async Task<long> CountAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await AuditRetentionQueries.CountAsync(db.Set<HostLogSourceAudit>(), request.Cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.Set<AgentSettingAudit>(), request.Cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.Set<AgentQueueCapacityAudit>(), request.Cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.Set<AgentMetricSettingAudit>(), request.Cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.Set<HostMetricThresholdAudit>(), request.Cutoff, token);
    }

    internal async Task<int> DeleteAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var removed = await AuditRetentionQueries.DeleteBatchAsync(db.Set<HostLogSourceAudit>(), request.Cutoff, request.BatchSize, token);
        if (removed < request.BatchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.Set<AgentSettingAudit>(), request.Cutoff, request.BatchSize - removed, token);
        if (removed < request.BatchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.Set<AgentQueueCapacityAudit>(), request.Cutoff, request.BatchSize - removed, token);
        if (removed < request.BatchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.Set<AgentMetricSettingAudit>(), request.Cutoff, request.BatchSize - removed, token);
        if (removed < request.BatchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.Set<HostMetricThresholdAudit>(), request.Cutoff, request.BatchSize - removed, token);
        await transaction.CommitAsync(token);
        return removed;
    }
}
