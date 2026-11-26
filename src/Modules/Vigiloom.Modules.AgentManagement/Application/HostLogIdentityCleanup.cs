using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Removes local Log identity markers only after the source Log is gone.</summary>
public sealed class HostLogIdentityCleanup(IDbContextFactory<HostMonitoringDbContext> contexts, IHostLogIdentities operations)
{
    public async Task<int> RunAsync(CancellationToken token)
    {
        var total = 0;
        long afterAgent = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agents = await db.InboxStates.AsNoTracking().Where(x => x.RowId > afterAgent && db.HostLogMessageIds.Any(m => m.AgentId == x.Id))
                .OrderBy(x => x.RowId).Select(x => new { x.Id, x.RowId }).Take(100).ToArrayAsync(token);
            if (agents.Length == 0) return total;
            foreach (var agent in agents)
            {
                afterAgent = agent.RowId;
                var afterMessage = Guid.Empty;
                while (true)
                {
                    var page = await CleanPageAsync(agent.Id, afterMessage, token);
                    total = checked(total + page.Removed);
                    if (page.Last is null) break;
                    afterMessage = page.Last.Value;
                }
            }
        }
    }

    private async Task<(Guid? Last, int Removed)> CleanPageAsync(Guid agentId, Guid after, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, agentId, token);
        if (await db.ReceivedBatches.AnyAsync(x => x.AgentId == agentId && EF.Property<bool>(x, "PendingDelivery"), token))
            return (null, 0);
        var ids = await db.HostLogMessageIds.FromSqlInterpolated($"""
            SELECT TOP (100) * FROM [agent].[HostLogMessageIds]
            WHERE [AgentId] = {agentId} AND [MessageId] > {after} ORDER BY [MessageId]
            """).AsNoTracking().Select(x => x.MessageId).ToArrayAsync(token);
        if (ids.Length == 0) return (null, 0);
        var existing = (await operations.ExistingAsync(agentId, ids, token)).ToHashSet();
        var removedIds = ids.Where(id => !existing.Contains(id)).ToArray();
        var removed = removedIds.Length == 0 ? 0 : await db.HostLogMessageIds
            .Where(x => x.AgentId == agentId && removedIds.Contains(x.MessageId)).ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
        return (ids[^1], removed);
    }
}
