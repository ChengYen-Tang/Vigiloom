using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

/// <summary>Incrementally incorporates pre-upgrade samples; each sample and its bucket commit together.</summary>
public sealed class PingRollupBackfill(IDbContextFactory<NetworkDbContext> contexts)
{
    public const int BatchSize = 50;

    public async Task<int> RunBatchAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.PingRollupBackfill',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """;
        var locked = (int)(await command.ExecuteScalarAsync(token) ?? -1);
        if (locked < 0) return 0;
        var samples = await db.PingSamples.FromSqlRaw("""
            SELECT TOP (50) * FROM [network].[PingSamples] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
            WHERE [Aggregated] = 0 ORDER BY [RowId]
            """).ToListAsync(token);
        foreach (var sample in samples)
        {
            await PingRollupWriter.AddAsync(db, sample, token);
            // Subsequent samples in this batch can belong to the same bucket.
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return samples.Count;
    }
}
