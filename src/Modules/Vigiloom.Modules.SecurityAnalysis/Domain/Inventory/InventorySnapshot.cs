using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

public enum InventorySnapshotStatus { Staging = 1, Complete = 2, Partial = 3, Expired = 4, Rejected = 5 }

/// <summary>Manifest and lifecycle of one bounded source generation.</summary>
public sealed class InventorySnapshot
{
    private InventorySnapshot() { }
    public InventorySnapshot(InventoryOwner owner, InventoryChunk chunk, DateTimeOffset now)
    {
        AgentId = owner.AgentId; DeviceId = owner.DeviceId; DeviceGeneration = owner.DeviceGeneration;
        Source = chunk.Source; Generation = chunk.Generation; SnapshotId = chunk.SnapshotId; SchemaVersion = chunk.SchemaVersion;
        Outcome = chunk.Outcome; ChunkCount = chunk.ChunkCount; TotalItems = chunk.TotalItems; TotalBytes = chunk.TotalBytes;
        Digest = chunk.Digest.ToArray(); ObservedAt = chunk.ObservedAt; StartedAt = now; Status = InventorySnapshotStatus.Staging;
    }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public InventorySource Source { get; private set; }
    public long Generation { get; private set; }
    public Guid SnapshotId { get; private set; }
    public int SchemaVersion { get; private set; }
    public InventoryOutcome Outcome { get; private set; }
    public int ChunkCount { get; private set; }
    public int TotalItems { get; private set; }
    public long TotalBytes { get; private set; }
    public byte[] Digest { get; private set; } = [];
    public DateTimeOffset ObservedAt { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public InventorySnapshotStatus Status { get; internal set; }
    public int ReceivedChunks { get; internal set; }
    public int ReceivedItems { get; internal set; }
    public long ReceivedBytes { get; internal set; }
}
