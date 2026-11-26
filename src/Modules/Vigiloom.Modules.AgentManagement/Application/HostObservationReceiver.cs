using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Stores runtime or metric batches with their durable receipt.</summary>
public sealed class HostObservationReceiver(IDbContextFactory<HostMonitoringDbContext> contexts, TimeProvider clock)
{
    public async Task<BatchReceiptStatus> ReceiveAsync(Guid agentId, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
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
        var prior = await db.ReceivedBatches.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.BatchId == batchId, token);
        if (prior is not null)
            return CryptographicOperations.FixedTimeEquals(prior.Digest, digest) ? BatchReceiptStatus.AlreadyAccepted : BatchReceiptStatus.Conflict;

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

        var now = clock.GetUtcNow();
        db.ReceivedBatches.Add(new ReceivedBatch(agentId, batchId, digest, enqueuedAt, now, ids.Length, payload.Length));
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
        await transaction.CommitAsync(token);
        return BatchReceiptStatus.Accepted;
    }
}
