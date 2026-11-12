namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Available inventory sources for the device's most recent Agent registration.</summary>
public sealed record DeviceInventoryView(Guid DeviceId, Guid? AgentId, IReadOnlyList<InventorySourceView> Sources);

/// <summary>Last collection attempt and the independently retained complete snapshot.</summary>
public sealed record InventorySourceView(InventorySource Source, DateTimeOffset? LastAttemptAt, InventoryOutcome? LastOutcome,
    InventoryCompletion? LastStatus, InventoryRejectionReason? LastRejectionReason, DateTimeOffset? LastCompleteAt,
    Guid? CurrentSnapshotId, DateTimeOffset? CurrentObservedAt, int? CurrentChunkCount, int? CurrentItemCount,
    Guid? PartialSnapshotId = null, DateTimeOffset? PartialObservedAt = null, int? PartialChunkCount = null, int? PartialItemCount = null,
    InventoryPartialReason? PartialReason = null);

/// <summary>A bounded chunk from a specific immutable current snapshot.</summary>
public sealed record InventorySnapshotChunkView(Guid SnapshotId, InventorySource Source, DateTimeOffset ObservedAt,
    int ChunkIndex, int ChunkCount, IReadOnlyList<InventoryEntry> Items);

public enum InventoryChangeType { Added = 1, Modified = 2, Removed = 3, NewlyObserved = 4, ObservedModified = 5 }

/// <summary>One change between complete source snapshots.</summary>
public sealed record InventoryChangeView(Guid AgentId, InventorySource Source, DateTimeOffset ObservedAt,
    InventoryChangeType Kind, InventoryEntry? Before, InventoryEntry? After);

/// <summary>A bounded page of private inventory changes; cursor is opaque.</summary>
public sealed record InventoryChangePage(IReadOnlyList<InventoryChangeView> Items, string? NextCursor);
