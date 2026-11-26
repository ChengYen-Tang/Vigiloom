using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed record AgentReceiverSnapshot(DateTimeOffset? LastCommittedBatchAt);
public interface IAgentReceiverMonitoring
{
    Task<DateTimeOffset?> LastCommittedBatchAsync(CancellationToken token);
}

/// <summary>Only reports committed receipts; no traffic is an unknown observation.</summary>
public sealed class AgentReceiverMonitoring(IDbContextFactory<AgentManagementDbContext> contexts) : IAgentReceiverMonitoring
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

public sealed class HostReceiverMonitoring(IDbContextFactory<HostMonitoringDbContext> contexts) : IAgentReceiverMonitoring
{
    public async Task<DateTimeOffset?> LastCommittedBatchAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var retained = await db.ReceivedBatches.AsNoTracking().Where(x => !EF.Property<bool>(x, "PendingDelivery"))
            .MaxAsync(x => (DateTimeOffset?)x.ReceivedAt, token);
        var cleaned = await db.InboxStates.AsNoTracking().MaxAsync(x => x.LastBatchReceivedAt, token);
        return retained is null || cleaned > retained ? cleaned : retained;
    }
}
