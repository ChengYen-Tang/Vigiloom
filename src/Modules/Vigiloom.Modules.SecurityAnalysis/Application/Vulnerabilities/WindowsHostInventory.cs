using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record WindowsHostEvidence(InventoryEntry Host, Guid SnapshotId, DateTimeOffset ObservedAt);

/// <summary>Loads the current, verified host snapshot for a resolved Agent owner.</summary>
internal static class WindowsHostInventory
{
    internal static async Task<WindowsHostEvidence?> LoadAsync(SecurityAnalysisDbContext db, InventoryOwner owner,
        DateTimeOffset now, TimeSpan maximumAge, CancellationToken token)
    {
        var state = await db.InventorySources.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId &&
            x.DeviceGeneration == owner.DeviceGeneration && x.Source == InventorySource.HostSystem, token);
        if (state?.CurrentGeneration is not { } generation || state.LastStatus != InventorySnapshotStatus.Complete || state.LastOutcome != InventoryOutcome.Complete)
            return null;
        var snapshot = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId &&
            x.DeviceGeneration == owner.DeviceGeneration && x.Source == InventorySource.HostSystem && x.Generation == generation &&
            x.Status == InventorySnapshotStatus.Complete && x.Outcome == InventoryOutcome.Complete, token);
        if (snapshot is null || snapshot.ChunkCount != 1 || snapshot.ObservedAt > now.AddMinutes(5) || snapshot.ObservedAt < now.Subtract(maximumAge)) return null;
        var data = await db.InventoryChunks.AsNoTracking().Where(x => x.AgentId == owner.AgentId && x.Source == InventorySource.HostSystem &&
            x.Generation == generation && x.ChunkIndex == 0).Select(x => x.Payload).SingleOrDefaultAsync(token);
        if (data is null || !InventoryChunkCodec.TryRead(data, out var chunk) || chunk!.ChunkIndex != 0 ||
            !InventoryManifest.Matches(snapshot, owner, chunk) || !SnapshotVerification.IsComplete([chunk]) ||
            chunk.Items.Count != 1 || chunk.Items[0].OperatingSystemFamily is null) return null;
        return new(chunk.Items[0], snapshot.SnapshotId, snapshot.ObservedAt);
    }
}
