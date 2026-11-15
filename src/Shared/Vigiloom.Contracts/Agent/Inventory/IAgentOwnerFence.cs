using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Locks and verifies an enrolled Agent and eligible device for a same-database transaction.</summary>
public interface IAgentOwnerFence
{
    Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token);
}
