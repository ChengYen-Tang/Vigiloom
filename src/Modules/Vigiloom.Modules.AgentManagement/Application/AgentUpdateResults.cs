using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum AgentUpdateResultStatus { Success, Invalid, Unauthorized, Conflict }

/// <summary>Stores authenticated update observations against their immutable targets.</summary>
public sealed class AgentUpdateResults(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity, TimeProvider clock)
{
    public async Task<AgentUpdateResultStatus> ReceiveAsync(string? credential, AgentUpdateResult report, CancellationToken token)
    {
        if (report is null || !report.IsValid()) return AgentUpdateResultStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return AgentUpdateResultStatus.Unauthorized;
        var target = await db.UpdateTargets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == report.UpdateId && x.AgentId == agent.Id, token);
        if (target is null || target.PackageId != report.PackageId || target.ManifestSha256 != report.ManifestSha256)
            return AgentUpdateResultStatus.Conflict;
        var existing = await db.UpdateReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agent.Id && x.MessageId == report.MessageId, token);
        if (existing is not null)
            return Same(existing, report) ? AgentUpdateResultStatus.Success : AgentUpdateResultStatus.Conflict;
        if (await db.UpdateReceipts.AnyAsync(x => x.UpdateId == report.UpdateId && x.Sequence == report.Sequence, token))
            return AgentUpdateResultStatus.Conflict;
        db.UpdateReceipts.Add(new AgentUpdateReceipt
        {
            AgentId = agent.Id, MessageId = report.MessageId, UpdateId = report.UpdateId, Sequence = report.Sequence,
            Result = report.Result, OccurredAt = report.OccurredAt, ReceivedAt = clock.GetUtcNow().ToUniversalTime(),
            RetryAllowed = report.RetryAllowed, Failure = report.Failure
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentUpdateResultStatus.Success;
    }

    private static bool Same(AgentUpdateReceipt existing, AgentUpdateResult report) =>
        existing.UpdateId == report.UpdateId && existing.Sequence == report.Sequence && existing.Result == report.Result &&
        existing.OccurredAt == report.OccurredAt && existing.RetryAllowed == report.RetryAllowed && existing.Failure == report.Failure;
}
