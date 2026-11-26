using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal sealed class AgentSecuritySettings(IDbContextFactory<SecurityAnalysisDbContext> contexts, IBaselineSource sources)
{
    public async Task<IntegrityScopeStatus> IntegrityStatusAsync(RegisteredAgentIdentity identity, Guid scopeId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var status = await new IntegrityStatusReader().ReadAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(identity.AgentId, identity.DeviceId, identity.DeviceGeneration), scopeId, token);
        await transaction.CommitAsync(token);
        return status;
    }

    public async Task<BaselineSettingsSnapshot> BaselineAsync(Guid deviceId, CancellationToken token)
    {
        var inventory = await InventoryAsync(deviceId, token);
        var checks = new List<BaselineSelection>();
        foreach (var key in await sources.GetKeysAsync(token))
        {
            var selected = await sources.GetActiveAsync(key, token);
            if (selected is { Availability: BaselineSourceAvailability.Ready, SourceId: not null,
                    RevisionId: not null, ActivationVersion: not null, Check: not null })
                checks.Add(new(selected.SourceId.Value, selected.RevisionId.Value, selected.ActivationVersion, selected.Check));
        }
        return BaselineSettingsSnapshot.Create(inventory.Settings.ScanIntervalSeconds, checks);
    }

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
