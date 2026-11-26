using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Incrementally folds Agent metrics into five-minute series.</summary>
public sealed class HostMetricRollupBackfill(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public const int BatchSize = 100;
    public async Task<int> RunBatchAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await HostMetricRollupCore.RunBatchAsync(db, token);
    }
}
