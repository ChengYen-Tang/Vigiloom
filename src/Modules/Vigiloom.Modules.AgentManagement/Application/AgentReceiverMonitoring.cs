using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Only reports committed receipts; no traffic is an unknown observation.</summary>
public sealed class AgentReceiverMonitoring(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<DateTimeOffset?> LastCommittedBatchAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var retained = await db.Agents.AsNoTracking()
            .SelectMany(agent => db.ReceivedBatches.Where(batch => batch.AgentId == agent.Id)
                .OrderByDescending(batch => batch.ReceivedAt).Select(batch => batch.ReceivedAt).Take(1))
            .MaxAsync(value => (DateTimeOffset?)value, token);
        var cleaned = await db.Agents.AsNoTracking().MaxAsync(agent => agent.LastBatchReceivedAt, token);
        return retained is null || cleaned > retained ? cleaned : retained;
    }
}
