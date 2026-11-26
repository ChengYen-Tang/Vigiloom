using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public interface IRecordedAgentTargetReader
{
    Task<RecordedAgentUpdateTarget?> GetAsync(Guid agentId, Guid updateId, CancellationToken token);
}

/// <summary>Persists update results for targets owned by Resources.</summary>
public sealed class HostUpdateResults(IDbContextFactory<HostMonitoringDbContext> contexts, IRecordedAgentTargetReader targets, TimeProvider clock)
{
    public async Task<AgentUpdateResultStatus> ReceiveAsync(RegisteredAgentIdentity identity, AgentUpdateResult report, CancellationToken token)
    {
        if (report is null || !report.IsValid()) return AgentUpdateResultStatus.Invalid;
        var target = await targets.GetAsync(identity.AgentId, report.UpdateId, token);
        if (target is null || target.PackageId != report.PackageId || target.ManifestSha256 != report.ManifestSha256)
            return AgentUpdateResultStatus.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, identity.AgentId, token);
        var existing = await db.UpdateReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == identity.AgentId && x.MessageId == report.MessageId, token);
        if (existing is not null) return AgentUpdateResults.Same(existing, report) ? AgentUpdateResultStatus.Success : AgentUpdateResultStatus.Conflict;
        if (await db.UpdateReceipts.AnyAsync(x => x.UpdateId == report.UpdateId && x.Sequence == report.Sequence, token)) return AgentUpdateResultStatus.Conflict;
        db.UpdateReceipts.Add(new AgentUpdateReceipt
        {
            AgentId = identity.AgentId, MessageId = report.MessageId, UpdateId = report.UpdateId, Sequence = report.Sequence,
            Result = report.Result, OccurredAt = report.OccurredAt, ReceivedAt = clock.GetUtcNow().ToUniversalTime(),
            RetryAllowed = report.RetryAllowed, Failure = report.Failure
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentUpdateResultStatus.Success;
    }

    public async Task<AgentVersionObservation?> GetAsync(Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.AgentStatuses.AsNoTracking().Where(x => x.Id == agentId)
            .Select(x => new { x.RuntimeVersion, x.RuntimeIdentifier, x.RuntimeReportedAt }).SingleOrDefaultAsync(token);
        var receipt = await db.UpdateReceipts.AsNoTracking()
            .Where(x => x.AgentId == agentId && !db.UpdateReceipts.Any(other => other.UpdateId == x.UpdateId && other.Sequence > x.Sequence))
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
        AgentUpdateResultView? latest = null;
        if (receipt is not null && await targets.GetAsync(agentId, receipt.UpdateId, token) is { } target)
            latest = new(receipt.UpdateId, target.PackageVersion, receipt.Result, receipt.OccurredAt, receipt.ReceivedAt, receipt.RetryAllowed, receipt.Failure);
        return agent is null && receipt is null ? null : new(agent?.RuntimeVersion, agent?.RuntimeIdentifier, agent?.RuntimeReportedAt, latest);
    }
}
