using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal sealed class AgentSecuritySettings(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    public async Task<InventorySettingsSnapshot> InventoryAsync(Guid deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await new InventorySettingsReader().ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), deviceId, token);
        await transaction.CommitAsync(token);
        return snapshot;
    }

    public async Task<IntegritySettingsSnapshot> IntegrityAsync(Guid deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await new IntegritySettingsReader().ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), deviceId, token);
        await transaction.CommitAsync(token);
        return snapshot;
    }
}
