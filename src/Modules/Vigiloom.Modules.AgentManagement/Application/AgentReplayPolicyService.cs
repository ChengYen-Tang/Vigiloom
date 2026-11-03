using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Controls the one-way rollout of the Agent replay window.</summary>
public sealed class AgentReplayPolicyService(IDbContextFactory<AgentManagementDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<AgentReplayPolicyView> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return View(await db.ReplayPolicies.AsNoTracking().SingleAsync(token));
    }

    public async Task<AgentReplayPolicyView?> TransitionAsync(AgentReplayPhase target, string version, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || target is not (AgentReplayPhase.Grace or AgentReplayPhase.Enforced))
            throw new ArgumentException("Invalid replay policy transition.");
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return null; }
        if (expected.Length != 8) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [Scope] = 1").SingleAsync(token);
        if (!expected.AsSpan().SequenceEqual(policy.Version)) return null;
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var previous = policy.Phase;
        var previousVersion = Convert.ToBase64String(policy.Version);
        var changed = target == AgentReplayPhase.Grace ? policy.Activate(now) : policy.Enforce(now);
        if (!changed) return null;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new AgentReplayPolicyChanged(previous, policy.Phase,
                policy.ActivatedAt!.Value, policy.GraceUntil!.Value, policy.EnforcedAt,
                previousVersion, Convert.ToBase64String(policy.Version))), token);
        var result = View(policy);
        await transaction.CommitAsync(token);
        return result;
    }

    private static AgentReplayPolicyView View(AgentReplayPolicy policy) => new(policy.Phase,
        policy.ActivatedAt, policy.GraceUntil, policy.EnforcedAt, Convert.ToBase64String(policy.Version));
}
