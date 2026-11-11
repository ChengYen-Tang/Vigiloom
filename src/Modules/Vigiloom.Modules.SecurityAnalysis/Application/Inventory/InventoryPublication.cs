using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Verifies and publishes a ready generation inside the caller's locked transaction.</summary>
internal static class InventoryPublication
{
    internal static async Task FinishAsync(SecurityAnalysisDbContext db, InventorySourceState state,
        InventorySnapshot snapshot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.AgentId != snapshot.AgentId || state.Source != snapshot.Source ||
            state.ActiveGeneration != snapshot.Generation || snapshot.Status != InventorySnapshotStatus.Staging ||
            snapshot.ReceivedChunks != snapshot.ChunkCount)
            throw new InvalidOperationException("Only the active, fully received snapshot can be finalized.");

        var incoming = await ReadVerifiedAsync(db, snapshot, cancellationToken);
        var status = incoming is null ? InventorySnapshotStatus.Rejected :
            snapshot.Outcome == InventoryOutcome.Complete ? InventorySnapshotStatus.Complete : InventorySnapshotStatus.Partial;

        if (status == InventorySnapshotStatus.Complete)
        {
            if (state.CurrentGeneration is long oldGeneration)
            {
                var old = await db.InventorySnapshots.SingleAsync(x => x.AgentId == state.AgentId &&
                    x.Source == state.Source && x.Generation == oldGeneration, cancellationToken);
                if (old.Status != InventorySnapshotStatus.Complete)
                    throw new InvalidOperationException("The current inventory snapshot is not complete.");
                var prior = await ReadVerifiedAsync(db, old, cancellationToken)
                    ?? throw new InvalidOperationException("The current inventory snapshot failed verification.");
                var changes = InventoryChanges.Compare(prior.SelectMany(x => x.Items), incoming!.SelectMany(x => x.Items));
                await InventoryStaging.DeleteChunksAsync(db, old, cancellationToken);
                var batch = new List<InventoryChangeRecord>(128);
                foreach (var change in changes)
                {
                    var key = change.After?.Key ?? change.Before!.Key;
                    batch.Add(new InventoryChangeRecord(snapshot, now, (int)change.Kind, key,
                        change.Before is null ? null : JsonSerializer.Serialize(change.Before),
                        change.After is null ? null : JsonSerializer.Serialize(change.After)));
                    if (batch.Count == 128) await SaveChangesAsync();
                }
                if (batch.Count > 0) await SaveChangesAsync();
                db.InventorySnapshots.Remove(old);

                async Task SaveChangesAsync()
                {
                    db.InventoryChanges.AddRange(batch);
                    await db.SaveChangesAsync(cancellationToken);
                    foreach (var record in batch) db.Entry(record).State = EntityState.Detached;
                    batch.Clear();
                }
            }
            state.CurrentGeneration = snapshot.Generation;
            state.LastCompleteAt = snapshot.ObservedAt;
        }
        else
        {
            // Failure or a partial read may never imply removal from the previous snapshot.
            await InventoryStaging.DeleteChunksAsync(db, snapshot, cancellationToken);
        }

        snapshot.Status = status;
        snapshot.FinishedAt = now;
        state.ClosedGeneration = snapshot.Generation;
        state.ActiveGeneration = null;
        state.LastAttemptAt = now;
        state.LastAttemptGeneration = snapshot.Generation;
        state.LastOutcome = snapshot.Outcome;
        state.LastStatus = status;
        state.LastRejectionReason = status == InventorySnapshotStatus.Rejected ? InventoryRejectionReason.Integrity : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<InventoryChunk[]?> ReadVerifiedAsync(SecurityAnalysisDbContext db,
        InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var payloads = db.InventoryChunks.AsNoTracking().Where(x => x.AgentId == snapshot.AgentId &&
            x.Source == snapshot.Source && x.Generation == snapshot.Generation)
            .OrderBy(x => x.ChunkIndex).Select(x => x.Payload).AsAsyncEnumerable();
        var chunks = new List<InventoryChunk>(snapshot.ChunkCount);
        var members = 0;
        await foreach (var payload in payloads.WithCancellation(cancellationToken))
        {
            if (!InventoryChunkCodec.TryRead(payload, out var chunk) ||
                !InventoryManifest.Matches(snapshot, new InventoryOwner(snapshot.AgentId, snapshot.DeviceId, snapshot.DeviceGeneration), chunk!))
                return null;
            foreach (var item in chunk!.Items)
            {
                members += item.Members?.Count ?? 0;
                if (members > InventoryChunkCodec.MaximumSnapshotMembers) return null;
            }
            chunks.Add(chunk);
        }
        return SnapshotVerification.IsComplete(chunks) ? chunks.ToArray() : null;
    }
}
