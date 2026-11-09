using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum BatchReceiptStatus { Accepted, AlreadyAccepted, Invalid, Unauthorized, Conflict, CapacityExceeded, ClockUncertain, Expired, TimestampAhead }

/// <summary>Commits a validated batch receipt and all supported observations atomically.</summary>
public sealed class AgentBatchReceiver(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity, TimeProvider clock,
    long capacityBytes = AgentBatchReceiver.MaximumRetainedBytesPerAgent, int maximumBatches = AgentBatchReceiver.MaximumRetainedBatchesPerAgent)
{
    public const long MaximumRetainedBytesPerAgent = 256L * 1024 * 1024;
    public const int MaximumRetainedBatchesPerAgent = 8192;

    public async Task<BatchReceiptStatus> ReceiveAsync(string? credential, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (!AgentBatchCodec.TryRead(payload.Span, out var batch)) return BatchReceiptStatus.Invalid;
        if (capacityBytes < 1 || maximumBatches < 1) throw new InvalidOperationException("Agent receipt capacity must be positive.");
        var digest = SHA256.HashData(payload.Span);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return BatchReceiptStatus.Unauthorized;
        var previous = await db.ReceivedBatches.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agent.Id && x.BatchId == batch!.BatchId, token);
        if (previous is not null)
            return CryptographicOperations.FixedTimeEquals(previous.Digest, digest) ? BatchReceiptStatus.AlreadyAccepted : BatchReceiptStatus.Conflict;

        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (HOLDLOCK) WHERE [Scope] = 1")
            .AsNoTracking().SingleAsync(token);
        // All Web nodes use the same database clock, and AuthenticateForWriteAsync
        // holds the device lock until commit. Existing acknowledgments remain valid
        // even if the platform clock has moved backwards.
        var policyTime = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        if (agent.ReplayClockFloor is { } floor && policyTime < floor ||
            policy.ActivatedAt is { } activated && policyTime < activated ||
            policy.EnforcedAt is { } enforced && policyTime < enforced)
        {
            agent.RecordReplayIssue(AgentReplayIssue.ClockUncertain, policyTime);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return BatchReceiptStatus.ClockUncertain;
        }
        if (agent.ReplayCleanedBefore is not null && policy.Phase != AgentReplayPhase.Enforced)
        {
            agent.RecordReplayIssue(AgentReplayIssue.ClockUncertain, policyTime);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return BatchReceiptStatus.ClockUncertain;
        }
        if (policy.Phase == AgentReplayPhase.Enforced)
        {
            // The policy HOLDLOCK serializes these reads with policy changes; all live previous
            // generations retain their promised allowance through the deadline instant.
            var previousWindow = await db.ReplayGenerations.AsNoTracking()
                .Where(x => x.PolicyId == policy.Id && x.ValidThrough >= policyTime)
                .MaxAsync(x => (int?)x.WindowSeconds, token);
            var effectiveWindow = Math.Max(policy.ReplayWindowSeconds, previousWindow ?? 0);
            var rejection = AgentReplayWindow.Classify(batch!.EnqueuedAt, policyTime,
                TimeSpan.FromSeconds(effectiveWindow), TimeSpan.FromMinutes(5), agent.ReplayCleanedBefore);
            if (rejection is { } reason)
            {
                agent.AdvanceReplayClock(policyTime);
                agent.RecordReplayIssue(reason == BatchReceiptStatus.Expired ? AgentReplayIssue.Expired : AgentReplayIssue.TimestampAhead, policyTime);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return reason;
            }
        }

        var itemIds = batch!.Items.Select(item => item.MessageId).ToArray();
        var existing = await db.RuntimeObservations.AsNoTracking().Where(x => x.AgentId == agent.Id && itemIds.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        foreach (var item in batch.Items)
            if (existing.TryGetValue(item.MessageId, out var found) && !found.Matches(item)) return BatchReceiptStatus.Conflict;

        var usage = await AgentReceiptUsage.ReadAsync(db, agent.Id, token);
        if (usage.Count >= maximumBatches || payload.Length > capacityBytes - usage.Bytes)
            return BatchReceiptStatus.CapacityExceeded;

        var receivedAt = clock.GetUtcNow();
        agent.AdvanceReplayClock(policyTime);
        agent.ClearReplayIssue();
        db.ReceivedBatches.Add(new ReceivedBatch(agent.Id, batch.BatchId, digest, batch.EnqueuedAt, receivedAt, batch.Items.Count, payload.Length, policyTime));
        for (var ordinal = 0; ordinal < batch.Items.Count; ordinal++)
        {
            var item = batch.Items[ordinal];
            if (!existing.ContainsKey(item.MessageId)) db.RuntimeObservations.Add(new RuntimeObservation(agent.Id, batch.BatchId, ordinal, item, receivedAt));
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return BatchReceiptStatus.Accepted;
    }
}
