using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Stores runtime or metric batches with their durable receipt.</summary>
public sealed class HostObservationReceiver(IDbContextFactory<HostMonitoringDbContext> contexts, TimeProvider clock)
{
    public async Task<BatchReceiptStatus> ReceiveAsync(RegisteredAgentIdentity identity, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var agentId = identity.AgentId;
        if (agentId.Version != 7) return BatchReceiptStatus.Unauthorized;
        IReadOnlyList<AgentRuntimeMessage>? runtimeItems = null;
        IReadOnlyList<AgentMetricPoint>? metricItems = null;
        Guid batchId;
        DateTimeOffset enqueuedAt;
        if (AgentBatchCodec.TryRead(payload.Span, out var runtime))
        {
            batchId = runtime!.BatchId;
            enqueuedAt = runtime.EnqueuedAt;
            runtimeItems = runtime.Items;
        }
        else if (AgentMetricBatchCodec.TryRead(payload.Span, out var metrics))
        {
            batchId = metrics!.BatchId;
            enqueuedAt = metrics.EnqueuedAt;
            metricItems = metrics.Items;
        }
        else return BatchReceiptStatus.Invalid;

        var digest = SHA256.HashData(payload.Span);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, agentId, token);
        var state = await db.InboxStates.SingleOrDefaultAsync(x => x.Id == agentId, token);
        if (state is not null && (state.DeviceId != identity.DeviceId || state.DeviceGeneration != identity.DeviceGeneration))
            return BatchReceiptStatus.Unauthorized;
        var prior = await db.ReceivedBatches.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.BatchId == batchId, token);
        if (prior is not null)
            return CryptographicOperations.FixedTimeEquals(prior.Digest, digest) ? BatchReceiptStatus.AlreadyAccepted : BatchReceiptStatus.Conflict;

        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (HOLDLOCK) WHERE [Scope] = 1")
            .AsNoTracking().SingleAsync(token);
        var policyTime = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        async Task<BatchReceiptStatus> RejectAsync(BatchReceiptStatus status, AgentReplayIssue issue, bool advanceClock)
        {
            state ??= db.InboxStates.Add(new HostInboxState { Id = agentId, DeviceId = identity.DeviceId, DeviceGeneration = identity.DeviceGeneration }).Entity;
            if (advanceClock && (state.ReplayClockFloor is null || policyTime > state.ReplayClockFloor)) state.ReplayClockFloor = policyTime;
            state.LastReplayIssue = issue;
            state.LastReplayIssueAt = policyTime;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return status;
        }
        if (state?.ReplayClockFloor is { } floor && policyTime < floor ||
            policy.ActivatedAt is { } activated && policyTime < activated ||
            policy.EnforcedAt is { } enforced && policyTime < enforced ||
            policy.CleanupCutoverAt is { } cutover && policyTime < cutover ||
            policy.InboxSettingsChangedAt is { } changed && policyTime < changed)
            return await RejectAsync(BatchReceiptStatus.ClockUncertain, AgentReplayIssue.ClockUncertain, false);
        if (state?.ReplayCleanedBefore is not null && policy.Phase != AgentReplayPhase.Enforced)
            return await RejectAsync(BatchReceiptStatus.ClockUncertain, AgentReplayIssue.ClockUncertain, false);
        if (policy.Phase == AgentReplayPhase.Enforced)
        {
            var previousWindow = await db.ReplayGenerations.AsNoTracking().Where(x => x.PolicyId == policy.Id && x.ValidThrough >= policyTime)
                .MaxAsync(x => (int?)x.WindowSeconds, token);
            var window = Math.Max(policy.ReplayWindowSeconds, previousWindow ?? 0);
            var rejection = AgentReplayWindow.Classify(enqueuedAt, policyTime, TimeSpan.FromSeconds(window),
                TimeSpan.FromSeconds(AgentInboxPolicyLimits.FutureSkewSeconds), state?.ReplayCleanedBefore);
            if (rejection is { } reason)
                return await RejectAsync(reason, reason == BatchReceiptStatus.Expired ? AgentReplayIssue.Expired : AgentReplayIssue.TimestampAhead, true);
        }

        var ids = runtimeItems is not null ? runtimeItems.Select(item => item.MessageId).ToArray() : metricItems!.Select(item => item.MessageId).ToArray();
        var runtimes = await db.RuntimeObservations.AsNoTracking().Where(x => x.AgentId == agentId && ids.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        var points = await db.HostMetrics.AsNoTracking().Where(x => x.AgentId == agentId && ids.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        if (runtimeItems is not null)
        {
            foreach (var item in runtimeItems)
                if (points.ContainsKey(item.MessageId) || runtimes.TryGetValue(item.MessageId, out var found) && !found.Matches(item))
                    return BatchReceiptStatus.Conflict;
        }
        else
        {
            foreach (var item in metricItems!)
                if (runtimes.ContainsKey(item.MessageId) || points.TryGetValue(item.MessageId, out var found) && !found.Matches(item))
                    return BatchReceiptStatus.Conflict;
        }

        long receiptCount;
        long receiptBytes;
        if (policy.CleanupCutoverAt is not null && state?.InboxReceiptCount is { } count && state.InboxReceiptPayloadBytes is { } bytes)
            (receiptCount, receiptBytes) = (count, bytes);
        else
        {
            var usage = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId)
                .GroupBy(x => x.AgentId).Select(group => new { Count = group.LongCount(), Bytes = group.Sum(x => (long)x.ByteCount) })
                .SingleOrDefaultAsync(token);
            (receiptCount, receiptBytes) = (usage?.Count ?? 0, usage?.Bytes ?? 0);
        }
        if (receiptCount >= AgentBatchReceiver.MaximumRetainedBatchesPerAgent ||
            payload.Length > AgentBatchReceiver.MaximumRetainedBytesPerAgent - receiptBytes)
            return BatchReceiptStatus.CapacityExceeded;

        var now = clock.GetUtcNow();
        state ??= db.InboxStates.Add(new HostInboxState { Id = agentId, DeviceId = identity.DeviceId, DeviceGeneration = identity.DeviceGeneration }).Entity;
        state.ReplayClockFloor = policyTime;
        state.LastReplayIssue = null;
        state.LastReplayIssueAt = null;
        var receipt = new ReceivedBatch(agentId, batchId, digest, enqueuedAt, now, ids.Length, payload.Length,
            policyTime, policyTime.AddDays(policy.ReceiptRetentionDays));
        db.ReceivedBatches.Add(receipt);
        if (runtimeItems is not null)
        {
            for (var i = 0; i < runtimeItems.Count; i++)
                if (!runtimes.ContainsKey(runtimeItems[i].MessageId)) db.RuntimeObservations.Add(new RuntimeObservation(agentId, batchId, i, runtimeItems[i], now));
        }
        else
        {
            for (var i = 0; i < metricItems!.Count; i++)
                if (!points.ContainsKey(metricItems[i].MessageId)) db.HostMetrics.Add(new HostMetric(agentId, batchId, i, metricItems[i], now));
        }
        await db.SaveChangesAsync(token);
        if (state.LastBatchReceivedAt is not { } last || now > last || now == last && receipt.RowId > (state.LastBatchReceiptRowId ?? 0))
        {
            state.LastBatchReceivedAt = now;
            state.LastBatchEnqueuedAt = enqueuedAt;
            state.LastBatchReceiptRowId = receipt.RowId;
        }
        if (policy.CleanupCutoverAt is not null)
        {
            state.InboxReceiptCount = checked(receiptCount + 1);
            state.InboxReceiptPayloadBytes = checked(receiptBytes + payload.Length);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return BatchReceiptStatus.Accepted;
    }
}
