using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Deletes expired host samples without discarding pending aggregation.</summary>
public sealed class HostMetricRetentionCleanup(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<(long Samples, long Rollups)> RunAsync(CancellationToken token)
    {
        await HostMetricRetentionSettings.EnsureAsync(contexts, token);
        long samples = 0, rollups = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var removed = await HostMetricRetentionCore.RunBatchAsync(db, token);
            samples += removed.Samples; rollups += removed.Rollups;
            if (removed.Samples < HostMetricRetentionCore.BatchSize && removed.Rollups < HostMetricRetentionCore.BatchSize) return (samples, rollups);
        }
    }
}
