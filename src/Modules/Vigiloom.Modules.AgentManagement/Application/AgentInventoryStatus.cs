using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Revalidates a machine credential while reading its own inventory progress.</summary>
public sealed class AgentInventoryStatus(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity,
    IInventoryStatusReader reader)
{
    public async Task<IReadOnlyList<InventorySourceStatus>?> GetAsync(string? credential, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, cancellationToken);
        if (agent is null) return null;
        var status = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(agent.Id, agent.DeviceId, agent.DeviceGeneration), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return status;
    }
}
