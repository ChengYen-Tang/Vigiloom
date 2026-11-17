using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Bounds outstanding update assignments across single-device and group operations.</summary>
internal static class AgentUpdateDispatch
{
    internal static async Task AcquireAsync(AgentManagementDbContext db, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'vigiloom.agent.update-dispatch', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0)
            throw new InvalidOperationException("Agent update dispatch lock could not be acquired.");
    }

    internal static async Task<bool> HasCapacityAsync(AgentManagementDbContext db, IDeviceGenerationSource devices, Guid excludingAgent, CancellationToken token)
    {
        var pending = await (from assignment in db.VersionAssignments.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on assignment.AgentId equals agent.Id
            where assignment.AgentId != excludingAgent && agent.RevokedAt == null && assignment.UpdateId != null &&
                !db.UpdateReceipts.Any(receipt => receipt.UpdateId == assignment.UpdateId &&
                    (receipt.Result == AgentUpdateResultKind.Succeeded || receipt.Result == AgentUpdateResultKind.RollbackFailed ||
                     receipt.Result == AgentUpdateResultKind.RolledBack && !receipt.RetryAllowed) &&
                    !db.UpdateReceipts.Any(later => later.UpdateId == receipt.UpdateId && later.Sequence > receipt.Sequence))
            select new { agent.DeviceId, agent.DeviceGeneration }).ToArrayAsync(token);
        var generations = await devices.ReadAsync(pending.Select(x => x.DeviceId).ToArray(), db.Database.GetDbConnection(),
            db.Database.CurrentTransaction!.GetDbTransaction(), token);
        return pending.Count(x => generations.TryGetValue(x.DeviceId, out var generation) && generation == x.DeviceGeneration)
            < AgentRolloutLimits.MaximumOutstanding;
    }

    internal static async Task<bool> IsOutstandingAsync(AgentManagementDbContext db, Guid updateId, CancellationToken token)
    {
        var last = await db.UpdateReceipts.AsNoTracking().Where(x => x.UpdateId == updateId)
            .OrderByDescending(x => x.Sequence).Select(x => new { x.Result, x.RetryAllowed }).FirstOrDefaultAsync(token);
        return last is null || last.Result is not (AgentUpdateResultKind.Succeeded or AgentUpdateResultKind.RollbackFailed) &&
            (last.Result != AgentUpdateResultKind.RolledBack || last.RetryAllowed);
    }
}
