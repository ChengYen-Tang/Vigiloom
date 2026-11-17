using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Deletes expired host samples and summaries without discarding pending aggregation.</summary>
public sealed class HostMetricRetentionCleanup(IDbContextFactory<AgentManagementDbContext> contexts)
{
    private const int BatchSize = 500;

    public async Task<(long Samples, long Rollups)> RunAsync(CancellationToken token)
    {
        await EnsureAsync(contexts, token);
        long samples = 0, rollups = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.HostMetricRollupBackfill',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                SELECT @result;
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return (samples, rollups);
            var policy = await db.HostMetricRetentionSettings.FromSqlRaw("SELECT * FROM [agent].[HostMetricRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var rawCutoff = now.AddDays(-policy.RawDays);
            var rollupCutoff = now.AddDays(-policy.RollupDays);
            var sampleIds = HostMetricRetentionEligibility.Samples(db, rawCutoff)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedSamples = await db.HostMetrics.Where(x => sampleIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var removedRollups = 0;
            if (removedSamples < BatchSize)
            {
                var rollupIds = HostMetricRetentionEligibility.Rollups(db, rollupCutoff)
                    .OrderBy(x => x.WindowStart).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
                removedRollups = await db.HostMetricRollups.Where(x => rollupIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            }
            await tx.CommitAsync(token);
            samples += removedSamples; rollups += removedRollups;
            if (removedSamples < BatchSize && removedRollups < BatchSize) return (samples, rollups);
        }
    }

    internal static async Task EnsureAsync(IDbContextFactory<AgentManagementDbContext> contexts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (await db.HostMetricRetentionSettings.AnyAsync(x => x.Slot == 1, token)) return;
        db.HostMetricRetentionSettings.Add(new HostMetricRetentionSetting());
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }
}
