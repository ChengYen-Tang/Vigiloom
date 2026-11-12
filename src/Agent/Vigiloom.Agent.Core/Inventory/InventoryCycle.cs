using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Inventory;

public enum InventoryCycleKind { Queued, QueuedFailure, Waiting, Complete, Rejected, Unavailable, OwnerChanged, QueueFull, TooLarge, ServerUnsupported }
public sealed record InventoryCycleResult(InventoryCycleKind Kind, InventorySource Source);

/// <summary>Coordinates one source generation; the caller must hold the inventory producer lease.</summary>
public sealed class InventoryCycle(InventoryStatusClient statusClient, TimeProvider clock)
{
    public async Task<InventoryCycleResult> RunAsync(IdentityVault vault, Uri server, InventorySource source,
        Func<InventorySource, CancellationToken, Task<InventoryReadResult<InventoryEntry>>> collect, CancellationToken token,
        AgentSettingsOwner? expectedOwner = null,
        Func<Func<CancellationToken, Task<EnqueueResult>>, CancellationToken, Task<EnqueueResult>>? enqueueUnderBudget = null)
    {
        ArgumentNullException.ThrowIfNull(collect);
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        var status = await statusClient.ReadAsync(vault, server, token);
        if (status.Kind != InventoryStatusResultKind.Success || status.Owner is null || status.Sources is null)
            return new(status.Kind == InventoryStatusResultKind.OwnerChanged ? InventoryCycleKind.OwnerChanged : InventoryCycleKind.Unavailable, source);
        if (expectedOwner is not null && status.Owner != expectedOwner) return new(InventoryCycleKind.OwnerChanged, source);
        var owner = status.Owner;
        var queue = new DurableBatchQueue(vault, owner.AgentId);
        var pending = await queue.ReadOutstandingInventoryAsync(owner, token);
        if (pending is not null)
        {
            var reconciliation = await queue.ReconcileInventoryAsync(owner, pending, status, token);
            return new(reconciliation switch
            {
                InventoryReconcileResult.Pending => InventoryCycleKind.Waiting,
                InventoryReconcileResult.Completed => InventoryCycleKind.Complete,
                InventoryReconcileResult.Rejected => InventoryCycleKind.Rejected,
                InventoryReconcileResult.OwnerChanged or InventoryReconcileResult.OutstandingChanged => InventoryCycleKind.OwnerChanged,
                _ => throw new InvalidOperationException("Unknown inventory reconciliation result.")
            }, pending.Source);
        }
        // A lost local queue must wait for central staging to finish or expire before beginning another source.
        if (status.Sources.Any(x => x.ActiveGeneration is not null)) return new(InventoryCycleKind.Waiting, source);
        if (source > InventorySource.HostVolumes && !status.SupportsProcessSources)
            return new(InventoryCycleKind.ServerUnsupported, source);
        var read = await collect(source, token);
        if (!Enum.IsDefined(read.Status)) throw new InvalidDataException("Invalid inventory collection status.");
        var afterRead = await statusClient.ReadAsync(vault, server, token);
        if (afterRead.Kind != InventoryStatusResultKind.Success || afterRead.Owner != owner || afterRead.Sources is null)
            return new(afterRead.Kind == InventoryStatusResultKind.OwnerChanged || afterRead.Owner is not null && afterRead.Owner != owner
                ? InventoryCycleKind.OwnerChanged : InventoryCycleKind.Unavailable, source);
        if (afterRead.Sources.Any(x => x.ActiveGeneration is not null)) return new(InventoryCycleKind.Waiting, source);
        if (source > InventorySource.HostVolumes && !afterRead.SupportsProcessSources)
            return new(InventoryCycleKind.ServerUnsupported, source);
        var highWater = afterRead.Sources.SingleOrDefault(x => x.Source == source)?.HighWaterGeneration ?? 0;
        var generation = await queue.ReserveInventoryGenerationAsync(owner, source, highWater, token);
        var now = clock.GetUtcNow();
        var snapshotId = Guid.CreateVersion7();
        var fallback = false;
        async Task<EnqueueResult> BuildAndEnqueueAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<QueuedBatch> batches;
            try { batches = InventoryChunkBuilder.Build(snapshotId, generation, source, now, now, (InventoryOutcome)read.Status, read.Items, read.PartialReason); }
            catch (ArgumentException)
            {
                // An oversized or invalid source is a failed read, never a complete empty snapshot.
                batches = InventoryChunkBuilder.Build(snapshotId, generation, source, now, now, InventoryOutcome.Error, []);
                fallback = true;
            }
            var result = await queue.EnqueueInventoryForOwnerAsync(batches, owner, source, generation, snapshotId, cancellationToken);
            if (result == EnqueueResult.ExceedsCapacity && !fallback)
            {
                // A snapshot larger than the configured queue can never fit by waiting for a drain.
                // Send a small failed-read outcome instead; the previous complete snapshot remains current.
                batches = InventoryChunkBuilder.Build(snapshotId, generation, source, now, now, InventoryOutcome.Error, []);
                fallback = true;
                result = await queue.EnqueueInventoryForOwnerAsync(batches, owner, source, generation, snapshotId, cancellationToken);
            }
            return result;
        }
        var queued = enqueueUnderBudget is null ? await BuildAndEnqueueAsync(token) : await enqueueUnderBudget(BuildAndEnqueueAsync, token);
        return new(queued switch
        {
            EnqueueResult.Queued or EnqueueResult.AlreadyQueued => fallback ? InventoryCycleKind.QueuedFailure : InventoryCycleKind.Queued,
            EnqueueResult.Full => InventoryCycleKind.QueueFull,
            EnqueueResult.ExceedsCapacity => InventoryCycleKind.TooLarge,
            EnqueueResult.OwnerChanged or EnqueueResult.ConflictingBatch => InventoryCycleKind.OwnerChanged,
            EnqueueResult.InventoryOutstanding => InventoryCycleKind.Waiting,
            _ => throw new InvalidOperationException("Unknown inventory queue result.")
        }, source);
    }
}
