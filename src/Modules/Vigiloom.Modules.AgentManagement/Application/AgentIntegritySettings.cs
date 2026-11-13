using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Authenticates and resolves the calling Agent's own integrity policy.</summary>
public sealed class AgentIntegritySettings(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity,
    IIntegritySettingsReader reader)
{
    public async Task<IntegritySettingsSnapshot?> GetAsync(string? credential, CancellationToken token)
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
