using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

/// <summary>Removes expired Ping samples and rollups without discarding samples awaiting aggregation.</summary>
public sealed class PingRetentionCleanup(IDbContextFactory<NetworkDbContext> contexts, PingSettings settings)
{
    private const int BatchSize = 500;

    public async Task<(long Samples, long Rollups)> RunAsync(CancellationToken token)
    {
        await settings.EnsureAsync(token);
        long samples = 0, rollups = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var policy = await db.PingSettings.FromSqlRaw("SELECT * FROM [network].[PingSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Name] = N'central'").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var sampleCutoff = now.AddDays(-policy.RawRetentionDays);
            var rollupCutoff = now.AddDays(-policy.RollupRetentionDays);
            var sampleIds = PingRetentionEligibility.Samples(db, sampleCutoff)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedSamples = await db.PingSamples.Where(x => sampleIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var rollupIds = PingRetentionEligibility.Rollups(db, rollupCutoff)
                .OrderBy(x => x.WindowStart).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedRollups = await db.PingRollups.Where(x => rollupIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
            samples += removedSamples;
            rollups += removedRollups;
            if (removedSamples < BatchSize && removedRollups < BatchSize) return (samples, rollups);
        }
    }
}
