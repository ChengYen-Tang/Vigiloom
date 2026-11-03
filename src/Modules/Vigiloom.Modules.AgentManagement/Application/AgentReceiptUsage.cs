using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

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
}
