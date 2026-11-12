using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>
/// Stages one chunk inside an already locked transaction. The caller must finalize a ready
/// snapshot and commit the transport receipt in the same transaction, or roll it all back.
/// </summary>
internal static class InventoryStaging
{
    internal enum Disposition { Stored, Ready, Conflict, Expired, Busy, TooLarge }

    internal sealed record Result(Disposition Disposition, InventorySourceState? State = null, InventorySnapshot? Snapshot = null);

    internal static async Task<Result> StoreAsync(SecurityAnalysisDbContext db, InventoryOwner owner, InventoryChunk chunk,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The receiver validates and authenticates before calling, and owns the per-Agent lock.
        var state = await db.InventorySources.SingleOrDefaultAsync(
            x => x.AgentId == owner.AgentId && x.Source == chunk.Source, cancellationToken);
        if (state is not null && (state.DeviceId != owner.DeviceId || state.DeviceGeneration != owner.DeviceGeneration))
            return new(Disposition.Conflict);
        if (state is not null && chunk.Generation <= state.ClosedGeneration)
            return new(Disposition.Expired);

        var otherActive = await db.InventorySources.AnyAsync(x => x.AgentId == owner.AgentId &&
            x.ActiveGeneration != null && x.Source != chunk.Source, cancellationToken);
        if (otherActive) return new(Disposition.Busy);
        if (state?.ActiveGeneration is long active && chunk.Generation < active)
            return new(Disposition.Expired);

        if (state?.ActiveGeneration is long previous && chunk.Generation > previous)
        {
            // A newer generation fences the old one; the transaction also contains the
            // new chunk and receipt, so rollback restores the previous staging state.
            var stale = await db.InventorySnapshots.SingleAsync(x => x.AgentId == owner.AgentId &&
                x.Source == chunk.Source && x.Generation == previous, cancellationToken);
            await CloseExpiredAsync(db, state, stale, now, cancellationToken);
        }

        var snapshot = state?.ActiveGeneration is null ? null : await db.InventorySnapshots.SingleAsync(
            x => x.AgentId == owner.AgentId && x.Source == chunk.Source && x.Generation == chunk.Generation, cancellationToken);
        if (snapshot is null)
        {
            var currentBytes = await (from source in db.InventorySources
                join current in db.InventorySnapshots on
                    new { source.AgentId, source.Source, Generation = source.CurrentGeneration } equals
                    new { current.AgentId, current.Source, Generation = (long?)current.Generation }
                where source.AgentId == owner.AgentId && (source.Source != chunk.Source || chunk.Outcome == InventoryOutcome.Partial)
                select (long?)current.TotalBytes).SumAsync(cancellationToken) ?? 0;
            var partialBytes = await (from source in db.InventorySources
                join partial in db.InventorySnapshots on
                    new { source.AgentId, source.Source, Generation = source.LastAttemptGeneration } equals
                    new { partial.AgentId, partial.Source, Generation = (long?)partial.Generation }
                where source.AgentId == owner.AgentId && source.Source != chunk.Source &&
                    partial.Source > InventorySource.HostVolumes && partial.Status == InventorySnapshotStatus.Partial &&
                    partial.Outcome == InventoryOutcome.Partial
                select (long?)partial.TotalBytes).SumAsync(cancellationToken) ?? 0;
            if ((chunk.Outcome == InventoryOutcome.Complete ||
                 chunk.Outcome == InventoryOutcome.Partial && chunk.Source > InventorySource.HostVolumes) &&
                currentBytes + partialBytes + chunk.TotalBytes > InventoryChunkCodec.MaximumSnapshotBytes)
            {
                if (state is null)
                {
                    state = new InventorySourceState(owner, chunk.Source);
                    db.InventorySources.Add(state);
                }
                await ClearLastPartialAsync(db, state, cancellationToken);
                var rejected = new InventorySnapshot(owner, chunk, now)
                {
                    Status = InventorySnapshotStatus.Rejected,
                    FinishedAt = now
                };
                db.InventorySnapshots.Add(rejected);
                state.ClosedGeneration = chunk.Generation;
                state.ActiveGeneration = null;
                state.LastAttemptAt = now;
                state.LastAttemptGeneration = chunk.Generation;
                state.LastOutcome = chunk.Outcome;
                state.LastStatus = InventorySnapshotStatus.Rejected;
                state.LastRejectionReason = InventoryRejectionReason.Capacity;
                await db.SaveChangesAsync(cancellationToken);
                return new(Disposition.TooLarge, state, rejected);
            }
            if (state is null)
            {
                state = new InventorySourceState(owner, chunk.Source);
                db.InventorySources.Add(state);
            }
            state.ActiveGeneration = chunk.Generation;
            snapshot = new InventorySnapshot(owner, chunk, now);
            db.InventorySnapshots.Add(snapshot);
        }
        else if (!InventoryManifest.Matches(snapshot, owner, chunk))
            return new(Disposition.Conflict);

        var payload = InventoryChunkCodec.Serialize(chunk);
        var existing = await db.InventoryChunks.SingleOrDefaultAsync(x => x.AgentId == owner.AgentId &&
            x.Source == chunk.Source && x.Generation == chunk.Generation && x.ChunkIndex == chunk.ChunkIndex, cancellationToken);
        if (existing is not null)
            return new(existing.Payload.AsSpan().SequenceEqual(payload) ?
                (snapshot.ReceivedChunks == snapshot.ChunkCount ? Disposition.Ready : Disposition.Stored) : Disposition.Conflict,
                state, snapshot);

        var measured = InventoryDigest.MeasureChunk(chunk.Items);
        if (snapshot.ReceivedChunks >= snapshot.ChunkCount ||
            chunk.Items.Count > snapshot.TotalItems - snapshot.ReceivedItems ||
            measured.Bytes > snapshot.TotalBytes - snapshot.ReceivedBytes)
            return new(Disposition.Conflict);

        db.InventoryChunks.Add(new InventoryChunkData(owner.AgentId, chunk, payload));
        snapshot.ReceivedChunks++;
        snapshot.ReceivedItems += chunk.Items.Count;
        snapshot.ReceivedBytes += measured.Bytes;
        await db.SaveChangesAsync(cancellationToken);
        return new(snapshot.ReceivedChunks == snapshot.ChunkCount ? Disposition.Ready : Disposition.Stored, state, snapshot);
    }

    internal static async Task CloseExpiredAsync(SecurityAnalysisDbContext db, InventorySourceState state,
        InventorySnapshot snapshot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.AgentId != snapshot.AgentId || state.Source != snapshot.Source ||
            state.ActiveGeneration != snapshot.Generation || snapshot.Status != InventorySnapshotStatus.Staging)
            throw new InvalidOperationException("Only the active source snapshot can expire.");
        await DeleteChunksAsync(db, snapshot, cancellationToken);
        await ClearLastPartialAsync(db, state, cancellationToken);
        snapshot.Status = InventorySnapshotStatus.Expired;
        snapshot.FinishedAt = now;
        state.ClosedGeneration = snapshot.Generation;
        state.ActiveGeneration = null;
        state.LastAttemptAt = now;
        state.LastAttemptGeneration = snapshot.Generation;
        state.LastOutcome = snapshot.Outcome;
        state.LastStatus = InventorySnapshotStatus.Expired;
        state.LastRejectionReason = null;
    }

    internal static async Task ClearLastPartialAsync(SecurityAnalysisDbContext db, InventorySourceState state, CancellationToken cancellationToken)
    {
        if (state.Source <= InventorySource.HostVolumes || state.LastAttemptGeneration is not long previousAttempt) return;
        var partial = await db.InventorySnapshots.SingleOrDefaultAsync(x => x.AgentId == state.AgentId &&
            x.Source == state.Source && x.Generation == previousAttempt && x.Status == InventorySnapshotStatus.Partial &&
            x.Outcome == InventoryOutcome.Partial, cancellationToken);
        if (partial is null) return;
        await DeleteChunksAsync(db, partial, cancellationToken);
        db.InventorySnapshots.Remove(partial);
    }

    internal static async Task DeleteChunksAsync(SecurityAnalysisDbContext db, InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        await db.InventoryChunks.Where(x => x.AgentId == snapshot.AgentId && x.Source == snapshot.Source &&
            x.Generation == snapshot.Generation).ExecuteDeleteAsync(cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries<InventoryChunkData>().Where(x =>
            x.Entity.AgentId == snapshot.AgentId && x.Entity.Source == snapshot.Source &&
            x.Entity.Generation == snapshot.Generation).ToArray())
            entry.State = EntityState.Detached;
    }
}
