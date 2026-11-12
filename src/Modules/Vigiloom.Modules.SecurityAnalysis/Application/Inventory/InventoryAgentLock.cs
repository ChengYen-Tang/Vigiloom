using System.Data.Common;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Serializes inventory quota, staging, publication and cleanup per Agent transaction.</summary>
internal static class InventoryAgentLock
{
    internal static Task<bool> TryAcquireAsync(DbConnection connection, DbTransaction transaction, Guid agentId,
        int timeoutMilliseconds, CancellationToken token) =>
        AgentSnapshotLock.TryAcquireAsync(connection, transaction, "Inventory", agentId, timeoutMilliseconds, token);
}
