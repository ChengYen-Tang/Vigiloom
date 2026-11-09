using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Transactional policy mutation with actor-bound confirmation for shorter obligations.</summary>
public sealed class AgentInboxSettings(IDbContextFactory<AgentManagementDbContext> contexts, IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.AgentInbox.Preview.v1");

    public async Task<AgentInboxSettingsSnapshot> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.ReplayPolicies.AsNoTracking().SingleAsync(token);
        var deleted = await db.Agents.SumAsync(x => (long?)x.InboxDeletedTotal, token) ?? 0;
        var last = await db.Agents.MaxAsync(x => x.InboxLastDeletedAt, token);
        return new(policy.ReplayWindowSeconds, policy.ReceiptRetentionDays, policy.CleanupCutoverAt is not null,
            deleted, last, Convert.ToBase64String(policy.Version));
    }

    /// <summary>Requires the caller to confirm old receivers have stopped and drained before invoking.</summary>
    public async Task<bool> ActivateCleanupAsync(string version, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actor));
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return false; }
        if (expected.Length != 8) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (XLOCK, HOLDLOCK) WHERE [Scope] = 1").SingleAsync(token);
        if (!expected.AsSpan().SequenceEqual(policy.Version)) return false;
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var previousVersion = Convert.ToBase64String(policy.Version);
        if (!policy.BeginCleanupCutover(now)) return false;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new AgentInboxCleanupActivated(now, policy.LegacyReceiptsRetainUntil!.Value,
                previousVersion, Convert.ToBase64String(policy.Version))), token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<AgentInboxSettingsPreview?> PreviewAsync(int replaySeconds, int retentionDays, string version, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || !AgentInboxPolicyLimits.IsValid(replaySeconds, retentionDays)) throw new ArgumentException("Invalid inbox policy or administrator.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.ReplayPolicies.AsNoTracking().SingleAsync(token);
        if (!string.Equals(version, Convert.ToBase64String(policy.Version), StringComparison.Ordinal) || policy.CleanupCutoverAt is null) return null;
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var receipts = await db.ReceivedBatches.LongCountAsync(token);
        var fenced = await db.Agents.LongCountAsync(x => x.ReplayCleanedBefore != null, token);
        var proof = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, replaySeconds, retentionDays, version, now)));
        return new(policy.ReplayWindowSeconds, policy.ReceiptRetentionDays, replaySeconds, retentionDays, receipts, fenced, now, version, proof);
    }

    public async Task<bool> UpdateAsync(int replaySeconds, int retentionDays, string version, string? confirmationToken, Guid actor, CancellationToken token)
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
        if ((replaySeconds < policy.ReplayWindowSeconds || retentionDays < policy.ReceiptRetentionDays) &&
            !ValidProof(confirmationToken, actor, replaySeconds, retentionDays, version, now))
            throw new ArgumentException("A current preview is required before shortening inbox obligations.", nameof(confirmationToken));
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

    private bool ValidProof(string? token, Guid actor, int replaySeconds, int retentionDays, string? version, DateTimeOffset now)
    {
        if (token is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<PreviewProof>(protector.Unprotect(token));
            return proof is not null && proof.Actor == actor && proof.ReplaySeconds == replaySeconds && proof.RetentionDays == retentionDays &&
                string.Equals(proof.Version, version, StringComparison.Ordinal) && proof.At.Offset == TimeSpan.Zero &&
                proof.At <= now && now - proof.At <= TimeSpan.FromMinutes(5);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return false; }
    }

    private sealed record PreviewProof(Guid Actor, int ReplaySeconds, int RetentionDays, string Version, DateTimeOffset At);
}
