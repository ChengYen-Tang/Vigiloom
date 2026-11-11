using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Authenticates and resolves only the calling Agent's inventory policy.</summary>
public sealed class AgentInventorySettings(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity,
    IInventorySettingsReader reader)
{
    public async Task<InventorySettingsSnapshot?> GetAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var snapshot = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), agent.DeviceId, token);
        await transaction.CommitAsync(token);
        return snapshot;
    }
}
