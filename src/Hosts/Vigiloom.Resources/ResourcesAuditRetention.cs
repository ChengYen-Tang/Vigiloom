using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Application.Retention;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Resources;

internal sealed class ResourcesAuditRetention(IDbContextFactory<ResourcesDbContext> contexts, DeviceAuditRetention retention)
{
    internal async Task<long> CountAsync(ResourcesAuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var count = await retention.CountAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, token);
        await transaction.CommitAsync(token);
        return count;
    }

    internal async Task<int> DeleteAsync(ResourcesAuditRetentionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var count = await retention.DeleteBatchAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), request.Cutoff, request.BatchSize, token);
        await transaction.CommitAsync(token);
        return count;
    }
}
