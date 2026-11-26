using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Aggregates and retains metrics in the independent host database.</summary>
public sealed class HostMetricMaintenance(IDbContextFactory<HostMonitoringDbContext> contexts, ILogger<HostMetricMaintenance> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RollupAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            if (await HostMetricRollupCore.RunBatchAsync(db, token) < HostMetricRollupBackfill.BatchSize) break;
        }
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task RetainAsync(CancellationToken token)
    {
        await using (var db = await contexts.CreateDbContextAsync(token)) await HostMetricRetentionSettings.EnsureAsync(db, token);
        long samples = 0, rollups = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var removed = await HostMetricRetentionCore.RunBatchAsync(db, token);
            samples += removed.Samples;
            rollups += removed.Rollups;
            if (removed.Samples < HostMetricRetentionCore.BatchSize && removed.Rollups < HostMetricRetentionCore.BatchSize) break;
        }
        logger.LogInformation("Host metric retention removed {SampleCount} samples and {RollupCount} rollups", samples, rollups);
    }
}
