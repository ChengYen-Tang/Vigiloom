using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Operations.ApplicationLogs;

/// <summary>Removes expired application Logs and their unused OTLP contexts.</summary>
public sealed class ApplicationLogCleanup(IDbContextFactory<CentralLogsDbContext> contexts)
{
    public async Task<ApplicationLogCleanupResult> RunAsync(int days, CancellationToken token)
    {
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days));
        const int batchSize = 500;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var ids = db.LogRecords.Where(x => x.OccurredAt < now.AddDays(-days))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize);
        var removed = await db.LogRecords.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        var contextsToDelete = db.LogContexts.Where(c => !db.LogRecords.Any(x => x.ContextId == c.RowId && x.ServiceId == c.ServiceId))
            .OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize);
        var removedContexts = await db.LogContexts.Where(x => contextsToDelete.Contains(x.RowId)).ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
        return new(removed, removedContexts, removed == batchSize || removedContexts == batchSize);
    }
}
