using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Verifies the current Agent and device. A co-located implementation may retain a lock for the transaction; a remote one cannot.</summary>
public interface IAgentOwnerFence
{
    Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token);
}
