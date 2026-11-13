using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Revalidates the calling Agent before reading its own scope progress.</summary>
public sealed class AgentIntegrityStatus(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity,
    IIntegrityStatusReader reader)
{
    public async Task<IntegrityScopeStatus?> GetAsync(string? credential, Guid scopeId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var status = await reader.ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(agent.Id, agent.DeviceId, agent.DeviceGeneration), scopeId, token);
        await transaction.CommitAsync(token);
        return status;
    }
}
