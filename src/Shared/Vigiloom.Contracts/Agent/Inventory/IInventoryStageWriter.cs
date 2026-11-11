using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>The authenticated owner is determined by the Agent receiver, never by inventory payload fields.</summary>
public sealed record InventoryOwner(Guid AgentId, Guid DeviceId, long DeviceGeneration);

public enum InventoryStageResult
{
    Accepted = 1, Conflict = 2, Expired = 3,
    /// <summary>An admissible snapshot is waiting for another staging generation to finish.</summary>
    CapacityExceeded = 4,
    /// <summary>The projected current snapshot set cannot fit and retrying unchanged cannot help.</summary>
    SnapshotTooLarge = 5
}

/// <summary>
/// Stages a chunk in the caller's open transaction; it never commits or owns the connection.
/// The caller must roll back the entire transaction on Conflict, Expired, CapacityExceeded or an exception.
/// SnapshotTooLarge persists a terminal rejection and must be committed without a successful receipt.
/// Accepted acknowledges the chunk; snapshot completion or rejection must be observed separately.
/// </summary>
public interface IInventoryStageWriter
{
    Task<InventoryStageResult> StageAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner,
        InventoryChunk chunk, DateTimeOffset databaseNow, CancellationToken cancellationToken);
}
