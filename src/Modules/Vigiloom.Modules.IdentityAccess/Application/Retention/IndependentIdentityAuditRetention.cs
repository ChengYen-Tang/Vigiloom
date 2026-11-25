using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Application.Retention;

/// <summary>Runs Identity retention in its own database transaction.</summary>
public sealed class IndependentIdentityAuditRetention(IDbContextFactory<IdentityAccessDbContext> contexts, IdentityAuditRetention retention)
{
    public async Task<long> CountAsync(DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var count = await retention.CountAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), cutoff, token);
        await transaction.CommitAsync(token);
        return count;
    }

    public async Task<int> DeleteBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var removed = await retention.DeleteBatchAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), cutoff, batchSize, token);
        await transaction.CommitAsync(token);
        return removed;
    }
}
