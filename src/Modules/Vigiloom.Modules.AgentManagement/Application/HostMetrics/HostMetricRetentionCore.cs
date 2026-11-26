using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

internal static class HostMetricRetentionCore
{
    internal const int BatchSize = 500;
    internal static async Task<(int Samples, int Rollups)> RunBatchAsync(DbContext db, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.HostMetricRollupBackfill',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return (0, 0);
        var policy = await db.Set<HostMetricRetentionSetting>().FromSqlRaw("SELECT * FROM [agent].[HostMetricRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var rawCutoff = now.AddDays(-policy.RawDays);
        var rollupCutoff = now.AddDays(-policy.RollupDays);
        var sampleIds = HostMetricRetentionEligibility.Samples(db, rawCutoff)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
        var removedSamples = await db.Set<HostMetric>().Where(x => sampleIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        var removedRollups = 0;
        if (removedSamples < BatchSize)
        {
            var rollupIds = HostMetricRetentionEligibility.Rollups(db, rollupCutoff)
                .OrderBy(x => x.WindowStart).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            removedRollups = await db.Set<HostMetricRollup>().Where(x => rollupIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        await tx.CommitAsync(token);
        return (removedSamples, removedRollups);
    }
}
