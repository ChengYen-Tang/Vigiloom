using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Count retained receipts and their original wire lengths for one Agent.</summary>
internal static class AgentReceiptUsage
{
    public static async Task<(long Count, long Bytes)> ReadAsync(AgentManagementDbContext db, Guid agentId, CancellationToken token)
    {
        var usage = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId)
            .GroupBy(x => x.AgentId).Select(group => new { Count = group.LongCount(), Bytes = group.Sum(x => (long)x.ByteCount) })
            .SingleOrDefaultAsync(token);
        return (usage?.Count ?? 0, usage?.Bytes ?? 0);
    }

    // Call only under the device transaction lock and after all old receivers have drained.
    public static async Task<(long Count, long Bytes)> InitializeAsync(AgentManagementDbContext db, Agent agent, CancellationToken token)
    {
        if (agent.InboxReceiptCount is { } count && agent.InboxReceiptPayloadBytes is { } bytes) return (count, bytes);
        var usage = await ReadAsync(db, agent.Id, token);
        agent.InitializeInboxUsage(usage.Count, usage.Bytes);
        return usage;
    }
}
