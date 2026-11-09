using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Transactional policy mutation; callers must authorize and validate any required preview first.</summary>
public sealed class AgentInboxSettings(IDbContextFactory<AgentManagementDbContext> contexts, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<bool> UpdateAsync(int replaySeconds, int retentionDays, string version, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || !AgentInboxPolicyLimits.IsValid(replaySeconds, retentionDays))
            throw new ArgumentException("Invalid inbox policy or administrator.");
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return false; }
        if (expected.Length != 8) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // HOLDLOCK alone or UPDLOCK does not block concurrent policy readers while adding generations.
        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (XLOCK, HOLDLOCK) WHERE [Scope] = 1").SingleAsync(token);
        if (!expected.AsSpan().SequenceEqual(policy.Version)) return false;
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var previousReplay = policy.ReplayWindowSeconds;
        var previousRetention = policy.ReceiptRetentionDays;
        var previousVersion = Convert.ToBase64String(policy.Version);
        var generation = policy.ChangeInboxSettings(replaySeconds, retentionDays, now);
        if (generation is not null) db.ReplayGenerations.Add(generation);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new AgentInboxSettingsChanged(previousReplay, previousRetention,
                replaySeconds, retentionDays, previousVersion, Convert.ToBase64String(policy.Version))), token);
        await transaction.CommitAsync(token);
        return true;
    }
}
