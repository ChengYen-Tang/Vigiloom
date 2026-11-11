using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Central progress for one authenticated Agent source; no inventory content is exposed.</summary>
public sealed record InventorySourceStatus(InventorySource Source, long HighWaterGeneration,
    long? ActiveGeneration, long? LastAttemptGeneration, InventoryOutcome? LastOutcome, InventoryCompletion? LastStatus,
    InventoryRejectionReason? LastRejectionReason, Guid? LastSnapshotId = null);

public enum InventoryCompletion { Complete = 2, Partial = 3, Expired = 4, Rejected = 5 }
public enum InventoryRejectionReason { Capacity = 1, Integrity = 2, Unknown = 3 }

/// <summary>Reads the Agent's own source progress inside its caller's authorized transaction.</summary>
public interface IInventoryStatusReader
{
    Task<IReadOnlyList<InventorySourceStatus>> ReadAsync(DbConnection connection, DbTransaction transaction,
        InventoryOwner owner, CancellationToken cancellationToken);
}
