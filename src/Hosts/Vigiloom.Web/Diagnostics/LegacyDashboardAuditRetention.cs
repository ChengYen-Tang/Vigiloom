using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Retention;

namespace Vigiloom.Web.Diagnostics;

internal sealed class LegacyDashboardAuditRetention(IDbContextFactory<SchedulingDbContext> contexts, DashboardAuditRetention retention)
{
    public async Task<long> CountAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        return await retention.CountAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, token);
    }

    public async Task<int> DeleteAsync(AuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var removed = await retention.DeleteBatchAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, request.BatchSize, token);
        await transaction.CommitAsync(token);
        return removed;
    }
}
