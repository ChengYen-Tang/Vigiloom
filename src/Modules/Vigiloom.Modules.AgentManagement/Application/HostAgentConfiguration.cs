using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves Agent scheduling settings from host-owned configuration.</summary>
public sealed class HostAgentConfiguration(IDbContextFactory<HostMonitoringDbContext> contexts, HostHeartbeat heartbeat)
{
    public async Task<AgentSettingsSnapshot?> GetForAgentAsync(RegisteredAgentIdentity identity, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, identity.AgentId, token);
        var agent = await heartbeat.GetOrCreateAsync(db, identity, token);
        if (agent is null) return null;
        var rows = await db.Set<AgentSetting>().FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {identity.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var snapshot = AgentConfiguration.ToView(identity.DeviceId, rows).Effective;
        agent.WidenHeartbeatSafety(snapshot.Settings.HeartbeatSeconds);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return snapshot;
    }
}
