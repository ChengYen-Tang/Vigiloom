using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum BatchReceiptStatus { Accepted, AlreadyAccepted, Invalid, Unauthorized, Conflict, CapacityExceeded }

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

        var itemIds = batch!.Items.Select(item => item.MessageId).ToArray();
        var existing = await db.RuntimeObservations.AsNoTracking().Where(x => x.AgentId == agent.Id && itemIds.Contains(x.MessageId))
            .ToDictionaryAsync(x => x.MessageId, token);
        foreach (var item in batch.Items)
            if (existing.TryGetValue(item.MessageId, out var found) && !found.Matches(item)) return BatchReceiptStatus.Conflict;

        var receipts = db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agent.Id);
        var count = await receipts.CountAsync(token);
        var bytes = await receipts.SumAsync(x => (long?)x.ByteCount, token) ?? 0;
        if (count >= maximumBatches || payload.Length > capacityBytes - bytes)
            return BatchReceiptStatus.CapacityExceeded;

        var receivedAt = clock.GetUtcNow();
        db.ReceivedBatches.Add(new ReceivedBatch(agent.Id, batch.BatchId, digest, batch.EnqueuedAt, receivedAt, batch.Items.Count, payload.Length));
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
