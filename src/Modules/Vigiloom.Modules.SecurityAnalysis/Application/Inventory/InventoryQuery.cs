using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Reads private, bounded inventory data for a resolved device and Agent identity.</summary>
public sealed class InventoryQuery(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    public async Task<DeviceInventoryView> GetAsync(Guid deviceId, Guid? agentId, CancellationToken token)
    {
        if (agentId is null) return new(deviceId, null, []);
        await using var db = await contexts.CreateDbContextAsync(token);
        var sources = await (from state in db.InventorySources.AsNoTracking()
            join snapshot in db.InventorySnapshots.AsNoTracking() on
                new { state.AgentId, state.Source, Generation = state.CurrentGeneration } equals
                new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation } into current
            from snapshot in current.DefaultIfEmpty()
            where state.AgentId == agentId && state.DeviceId == deviceId
            orderby state.Source
            select new InventorySourceView(state.Source, state.LastAttemptAt, state.LastOutcome,
                (InventoryCompletion?)state.LastStatus, state.LastRejectionReason, state.LastCompleteAt,
                snapshot == null ? null : snapshot.SnapshotId, snapshot == null ? null : snapshot.ObservedAt,
                snapshot == null ? null : snapshot.ChunkCount, snapshot == null ? null : snapshot.TotalItems)).ToArrayAsync(token);
        return new(deviceId, agentId, sources);
    }

    public async Task<InventorySnapshotChunkView?> GetChunkAsync(Guid deviceId, Guid agentId, InventorySource source,
        Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        if (!Enum.IsDefined(source) || snapshotId.Version != 7 || chunkIndex is < 0 or >= InventoryChunkCodec.MaximumChunks)
            throw new ArgumentException("Invalid inventory snapshot query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var payload = await (from state in db.InventorySources.AsNoTracking()
            join snapshot in db.InventorySnapshots.AsNoTracking() on
                new { state.AgentId, state.Source, Generation = state.CurrentGeneration } equals
                new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation }
            join chunk in db.InventoryChunks.AsNoTracking() on
                new { snapshot.AgentId, snapshot.Source, snapshot.Generation } equals new { chunk.AgentId, chunk.Source, chunk.Generation }
            where state.AgentId == agentId && state.DeviceId == deviceId && state.Source == source &&
                snapshot.SnapshotId == snapshotId && chunk.ChunkIndex == chunkIndex
            select chunk.Payload).SingleOrDefaultAsync(token);
        if (payload is null) return null;
        if (!InventoryChunkCodec.TryRead(payload, out var parsed) || parsed!.SnapshotId != snapshotId ||
            parsed.Source != source || parsed.ChunkIndex != chunkIndex)
            throw new InvalidDataException("The saved inventory chunk is invalid.");
        return new(parsed.SnapshotId, parsed.Source, parsed.ObservedAt, parsed.ChunkIndex, parsed.ChunkCount, parsed.Items);
    }
}
