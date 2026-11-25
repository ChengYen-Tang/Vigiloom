using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads the most recent Agent update result for display.</summary>
public static class AgentVersionObservations
{
    public static Task<AgentUpdateResultView?> LatestAsync(AgentManagementDbContext db, Guid agentId, CancellationToken token) =>
        (from receipt in db.UpdateReceipts.AsNoTracking()
         join target in db.UpdateTargets.AsNoTracking() on receipt.UpdateId equals target.Id
         where receipt.AgentId == agentId && !db.UpdateReceipts.Any(other => other.UpdateId == receipt.UpdateId && other.Sequence > receipt.Sequence)
         orderby receipt.ReceivedAt descending, receipt.RowId descending
         select new AgentUpdateResultView(receipt.UpdateId, target.Version, receipt.Result,
             receipt.OccurredAt, receipt.ReceivedAt, receipt.RetryAllowed, receipt.Failure)).FirstOrDefaultAsync(token);
}
