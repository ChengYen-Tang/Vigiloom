using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum BatchReceiptStatus { Accepted, AlreadyAccepted, Invalid, Unauthorized, Conflict, CapacityExceeded, ClockUncertain, Expired, TimestampAhead, RateLimited, LogBatchTooLarge }
public readonly record struct BatchReceiptResult(BatchReceiptStatus Status, TimeSpan? RetryAfter = null)
{
    public static implicit operator BatchReceiptResult(BatchReceiptStatus status) => new(status);
}

/// <summary>Commits a validated batch receipt and all supported observations atomically.</summary>
public sealed class AgentBatchReceiver(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity, TimeProvider clock,
    long capacityBytes = AgentBatchReceiver.MaximumRetainedBytesPerAgent, int maximumBatches = AgentBatchReceiver.MaximumRetainedBatchesPerAgent)
{
    public const long MaximumRetainedBytesPerAgent = 4L * 1024 * 1024 * 1024;
    public const int MaximumRetainedBatchesPerAgent = 1_000_000;

    public async Task<BatchReceiptStatus> ReceiveAsync(string? credential, ReadOnlyMemory<byte> payload, CancellationToken token) =>
        (await ReceiveDetailedAsync(credential, payload, token)).Status;

    public async Task<BatchReceiptResult> ReceiveDetailedAsync(string? credential, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        AgentMetricBatch? metrics = null;
        AgentLogBatch? logs = null;
        if (!AgentBatchCodec.TryRead(payload.Span, out var batch) &&
            !AgentMetricBatchCodec.TryRead(payload.Span, out metrics) &&
            !AgentLogBatchCodec.TryRead(payload.Span, out logs)) return BatchReceiptStatus.Invalid;
        var batchId = batch?.BatchId ?? metrics?.BatchId ?? logs!.BatchId;
        var enqueuedAt = batch?.EnqueuedAt ?? metrics?.EnqueuedAt ?? logs!.EnqueuedAt;
        var itemCount = batch?.Items.Count ?? metrics?.Items.Count ?? logs!.Items.Count;
        if (capacityBytes < 1 || maximumBatches < 1) throw new InvalidOperationException("Agent receipt capacity must be positive.");
        var digest = SHA256.HashData(payload.Span);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return BatchReceiptStatus.Unauthorized;
        var previous = await db.ReceivedBatches.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agent.Id && x.BatchId == batchId, token);
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
            policy.EnforcedAt is { } enforced && policyTime < enforced ||
            policy.CleanupCutoverAt is { } cutover && policyTime < cutover ||
            policy.InboxSettingsChangedAt is { } changed && policyTime < changed)
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
            var rejection = AgentReplayWindow.Classify(enqueuedAt, policyTime,
                TimeSpan.FromSeconds(effectiveWindow), TimeSpan.FromSeconds(AgentInboxPolicyLimits.FutureSkewSeconds), agent.ReplayCleanedBefore);
            if (rejection is { } reason)
            {
                agent.AdvanceReplayClock(policyTime);
                agent.RecordReplayIssue(reason == BatchReceiptStatus.Expired ? AgentReplayIssue.Expired : AgentReplayIssue.TimestampAhead, policyTime);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return reason;
            }
        }

        var itemIds = batch is not null ? batch.Items.Select(item => item.MessageId).ToArray() :
            metrics is not null ? metrics.Items.Select(item => item.MessageId).ToArray() : logs!.Items.Select(item => item.MessageId).ToArray();
        var existingRuntime = await db.RuntimeObservations.AsNoTracking().Where(x => x.AgentId == agent.Id && itemIds.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        var existingMetrics = await db.HostMetrics.AsNoTracking().Where(x => x.AgentId == agent.Id && itemIds.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        var existingLogs = await db.HostLogs.AsNoTracking().Where(x => x.AgentId == agent.Id && itemIds.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        if (batch is not null)
        {
            foreach (var item in batch.Items)
                if (existingMetrics.ContainsKey(item.MessageId) || existingLogs.ContainsKey(item.MessageId) ||
                    existingRuntime.TryGetValue(item.MessageId, out var found) && !found.Matches(item)) return BatchReceiptStatus.Conflict;
        }
        else if (metrics is not null)
        {
            foreach (var item in metrics.Items)
                if (existingRuntime.ContainsKey(item.MessageId) || existingLogs.ContainsKey(item.MessageId) ||
                    existingMetrics.TryGetValue(item.MessageId, out var found) && !found.Matches(item)) return BatchReceiptStatus.Conflict;
        }
        else
        {
            foreach (var item in logs!.Items)
                if (existingRuntime.ContainsKey(item.MessageId) || existingMetrics.ContainsKey(item.MessageId) ||
                    existingLogs.TryGetValue(item.MessageId, out var found) && !found.Matches(logs, item)) return BatchReceiptStatus.Conflict;
        }

        var usageWasInitialized = agent.InboxReceiptCount is not null;
        var usage = policy.CleanupCutoverAt is not null
            ? await AgentReceiptUsage.InitializeAsync(db, agent, token)
            : await AgentReceiptUsage.ReadAsync(db, agent.Id, token);
        if (usage.Count >= maximumBatches || payload.Length > capacityBytes - usage.Bytes)
        {
            if (policy.CleanupCutoverAt is not null && !usageWasInitialized)
            {
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            return BatchReceiptStatus.CapacityExceeded;
        }

        if (logs is not null)
        {
            var newCount = logs.Items.Count(item => !existingLogs.ContainsKey(item.MessageId));
            if (newCount > 0)
            {
                var admission = await LogAdmission.AdmitAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                    new(LogSourceKind.Agent, agent.Id), newCount, token);
                if (admission.Status == LogAdmissionStatus.BatchTooLarge) return BatchReceiptStatus.LogBatchTooLarge;
                if (admission.Status != LogAdmissionStatus.Accepted) return new(BatchReceiptStatus.RateLimited, admission.RetryAfter);
            }
        }

        var receivedAt = clock.GetUtcNow();
        agent.AdvanceReplayClock(policyTime);
        agent.ClearReplayIssue();
        if (policy.CleanupCutoverAt is not null) agent.ChangeInboxUsage(1, payload.Length);
        db.ReceivedBatches.Add(new ReceivedBatch(agent.Id, batchId, digest, enqueuedAt, receivedAt, itemCount, payload.Length,
            policyTime, policyTime.AddDays(policy.ReceiptRetentionDays)));
        for (var ordinal = 0; ordinal < itemCount; ordinal++)
        {
            if (batch is not null)
            {
                var item = batch.Items[ordinal];
                if (!existingRuntime.ContainsKey(item.MessageId)) db.RuntimeObservations.Add(new RuntimeObservation(agent.Id, batchId, ordinal, item, receivedAt));
            }
            else if (metrics is not null)
            {
                var item = metrics.Items[ordinal];
                if (!existingMetrics.ContainsKey(item.MessageId)) db.HostMetrics.Add(new HostMetric(agent.Id, batchId, ordinal, item, receivedAt));
            }
            else if (!existingLogs.ContainsKey(logs!.Items[ordinal].MessageId)) db.HostLogs.Add(new HostLog(agent.Id, logs, ordinal, receivedAt));
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return BatchReceiptStatus.Accepted;
    }
}
