using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.ServiceDetails;

internal sealed class LegacyHostLogCleanup(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IHostLogRetention hostLogs, ISecurityDetectionRetention detections)
{
    public async Task<HostLogCleanupBatch> RunAsync(int days, CancellationToken token)
    {
        const int batchSize = 500;
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days));
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var removed = await detections.DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), now, days, batchSize, token);
        await tx.CommitAsync(token);
        if (removed < batchSize)
        {
            await using var hostTx = await db.Database.BeginTransactionAsync(token);
            removed += await hostLogs.DeleteBatchAsync(db.Database.GetDbConnection(), hostTx.GetDbTransaction(), now, days, batchSize - removed, token);
            await hostTx.CommitAsync(token);
        }
        return new(removed, removed == batchSize);
    }
}
