using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record WindowsPatchEvidence(InventorySource Source, Guid SnapshotId, InventoryOutcome Outcome,
    DateTimeOffset ObservedAt, IReadOnlyList<InventoryEntry> Items);

/// <summary>Loads a verified recent observation; a partial WUA cache is never a complete update inventory.</summary>
internal static class WindowsPatchInventory
{
    internal static async Task<WindowsPatchEvidence?> LoadAsync(SecurityAnalysisDbContext db, InventoryOwner owner,
        InventorySource source, DateTimeOffset now, TimeSpan maximumAge, CancellationToken token)
    {
        if (source is not (InventorySource.WindowsQuickFix or InventorySource.WindowsUpdate)) throw new ArgumentOutOfRangeException(nameof(source));
        var state = await db.InventorySources.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId &&
            x.DeviceGeneration == owner.DeviceGeneration && x.Source == source, token);
        if (state?.LastAttemptGeneration is not { } generation || state.LastStatus is not (InventorySnapshotStatus.Complete or InventorySnapshotStatus.Partial) ||
            state.LastOutcome is not (InventoryOutcome.Complete or InventoryOutcome.Partial)) return null;
        var snapshot = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId &&
            x.DeviceGeneration == owner.DeviceGeneration && x.Source == source && x.Generation == generation, token);
        if (snapshot is null || snapshot.Status != state.LastStatus || snapshot.Outcome != state.LastOutcome ||
            snapshot.ObservedAt > now.AddMinutes(5) || snapshot.ObservedAt < now.Subtract(maximumAge)) return null;
        var chunks = new List<InventoryChunk>(snapshot.ChunkCount);
        var payloads = db.InventoryChunks.AsNoTracking().Where(x => x.AgentId == owner.AgentId && x.Source == source && x.Generation == generation)
            .OrderBy(x => x.ChunkIndex).Select(x => x.Payload).AsAsyncEnumerable();
        await foreach (var payload in payloads.WithCancellation(token))
        {
            if (!InventoryChunkCodec.TryRead(payload, out var chunk) || !InventoryManifest.Matches(snapshot, owner, chunk!)) return null;
            chunks.Add(chunk!);
        }
        if (!SnapshotVerification.IsComplete(chunks)) return null;
        return new(source, snapshot.SnapshotId, snapshot.Outcome, snapshot.ObservedAt, chunks.SelectMany(x => x.Items).ToArray());
    }
}
