using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Application.Retention;

/// <summary>Retains the initialization marker independently of ordinary account and credential history.</summary>
public sealed class IdentityAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var accounts = await db.AccountAuditEvents.LongCountAsync(x => x.OccurredAt < cutoff && x.Action != "InitialAdministratorCreated", token);
        var credentials = await db.ApiCredentialAuditEvents.LongCountAsync(x => x.OccurredAt < cutoff, token);
        return accounts + credentials;
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var accountIds = db.AccountAuditEvents.Where(x => x.OccurredAt < cutoff && x.Action != "InitialAdministratorCreated")
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize);
        var accounts = await db.AccountAuditEvents.Where(x => accountIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        if (accounts == batchSize) return accounts;
        var credentialIds = db.ApiCredentialAuditEvents.Where(x => x.OccurredAt < cutoff)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - accounts);
        var credentials = await db.ApiCredentialAuditEvents.Where(x => credentialIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        return accounts + credentials;
    }

    private static async Task<IdentityAccessDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<IdentityAccessDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options;
        var db = new IdentityAccessDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}
