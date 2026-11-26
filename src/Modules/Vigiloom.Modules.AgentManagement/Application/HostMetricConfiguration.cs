using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves and persists Agent metric settings in the host database.</summary>
public sealed class HostMetricConfiguration(IDbContextFactory<HostMonitoringDbContext> contexts, TimeProvider clock)
{
    public async Task<AgentMetricSettingsView> GetAsync(Guid? deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.Set<AgentMetricSetting>().AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
        var result = AgentMetricConfiguration.ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<AgentMetricSettingsSnapshot> GetForAgentAsync(RegisteredAgentIdentity identity, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rows = await db.Set<AgentMetricSetting>().FromSqlInterpolated($"SELECT * FROM [agent].[MetricSettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {identity.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var snapshot = AgentMetricConfiguration.ToView(identity.DeviceId, rows).Effective;
        await transaction.CommitAsync(token);
        return snapshot;
    }

    public async Task<AgentMetricSettingsView?> UpdateAsync(Guid? deviceId, AgentMetricSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update?.Overrides is null || !AgentMetricConfiguration.Valid(update.Overrides))
            throw new ArgumentException("Invalid Agent metric update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await AgentMetricSettingsWriter.UpdateAsync(db, deviceId, update, actorId, clock.GetUtcNow(), version, token);
    }
}
