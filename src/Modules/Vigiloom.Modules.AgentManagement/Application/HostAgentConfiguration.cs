using Microsoft.EntityFrameworkCore;
using System.Data;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves Agent scheduling settings from host-owned configuration.</summary>
public sealed class HostAgentConfiguration(IDbContextFactory<HostMonitoringDbContext> contexts, HostHeartbeat heartbeat, TimeProvider clock)
{
    public async Task<AgentSettingsView> GetAsync(Guid? deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.Set<AgentSetting>().AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
        var result = AgentConfiguration.ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<AgentSettingsView?> UpdateAsync(Guid? deviceId, AgentSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update is null || !AgentConfiguration.Valid(update.Overrides))
            throw new ArgumentException("Invalid Agent settings update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await AgentTransportSettingsWriter.UpdateAsync(db, deviceId, update, actorId, clock.GetUtcNow(), version, token);
    }

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
