using System.Security.Cryptography;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Checks that a staged chunk belongs to the original authenticated snapshot.</summary>
internal static class InventoryManifest
{
    internal static bool Matches(InventorySnapshot snapshot, InventoryOwner owner, InventoryChunk chunk) =>
        snapshot.AgentId == owner.AgentId && snapshot.DeviceId == owner.DeviceId &&
        snapshot.DeviceGeneration == owner.DeviceGeneration && snapshot.Source == chunk.Source &&
        snapshot.Generation == chunk.Generation && snapshot.SnapshotId == chunk.SnapshotId &&
        snapshot.SchemaVersion == chunk.SchemaVersion && snapshot.Outcome == chunk.Outcome &&
        snapshot.ChunkCount == chunk.ChunkCount && snapshot.TotalItems == chunk.TotalItems &&
        snapshot.TotalBytes == chunk.TotalBytes && snapshot.ObservedAt == chunk.ObservedAt &&
        CryptographicOperations.FixedTimeEquals(snapshot.Digest, chunk.Digest);
}
