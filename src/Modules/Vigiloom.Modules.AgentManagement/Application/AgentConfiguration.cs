using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves and audits administrator-managed Agent transport scheduling.</summary>
public sealed class AgentConfiguration(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, AgentIdentity identity, TimeProvider clock)
{
    /// <summary>Read a layer and its effective values; null means the device does not exist.</summary>
    public async Task<AgentSettingsView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        if (deviceId is { } id && (id == Guid.Empty || await devices.GetAsync(id, token) is null)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await ReadLayersAsync(db, deviceId, token);
        var view = ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return view;
    }

    /// <summary>Resolve only the authenticated machine's device, checking authorization in the read transaction.</summary>
    public async Task<AgentSettingsSnapshot?> GetForAgentAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var rows = await db.Settings.FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {agent.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var snapshot = ToView(agent.DeviceId, rows).Effective;
        // Persist the conservative threshold before the longer interval can leave the server.
        agent.WidenHeartbeatSafety(snapshot.Settings.HeartbeatSeconds);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return snapshot;
    }

    /// <summary>Save one layer and its audit atomically; null means a conflicting version.</summary>
    public async Task<AgentSettingsView?> UpdateAsync(Guid? deviceId, AgentSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update is null || update.Overrides is null || !Valid(update.Overrides))
            throw new ArgumentException("Invalid Agent settings update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        if (deviceId is { } id && await devices.GetAsync(id, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        // Update locks serialize competing edits, including the missing platform or device row.
        var rows = await db.Settings.FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}")
            .ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new AgentSetting { DeviceId = deviceId };
            db.Settings.Add(setting);
            rows.Add(setting);
        }
        else
        {
            if (version.Length != 8 || !version.AsSpan().SequenceEqual(setting.Version)) return null;
            db.Entry(setting).Property(x => x.Version).OriginalValue = version;
        }
        setting.HeartbeatSeconds = update.Overrides.HeartbeatSeconds;
        setting.SettingsPollSeconds = update.Overrides.SettingsPollSeconds;
        setting.BatchIntervalSeconds = update.Overrides.BatchIntervalSeconds;
        setting.UpdatedAt = clock.GetUtcNow();
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var view = ToView(deviceId, rows);
        try
        {
            await db.SaveChangesAsync(token);
            db.SettingAudits.Add(new AgentSettingAudit
            {
                SettingId = setting.Id, DeviceId = deviceId, ActorId = actorId, OccurredAt = setting.UpdatedAt,
                HeartbeatSeconds = setting.HeartbeatSeconds, SettingsPollSeconds = setting.SettingsPollSeconds,
                BatchIntervalSeconds = setting.BatchIntervalSeconds,
                EffectiveHeartbeatSeconds = view.Effective.Settings.HeartbeatSeconds,
                EffectiveSettingsPollSeconds = view.Effective.Settings.SettingsPollSeconds,
                EffectiveBatchIntervalSeconds = view.Effective.Settings.BatchIntervalSeconds,
                EffectiveRevision = view.Effective.Revision, ChangeVersion = setting.Version.ToArray()
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }

    public static bool Valid(AgentSettingsOverrides? layer) => layer is not null && Resolve(AgentSettings.Default, layer).IsValid();
    private static AgentSettings Resolve(AgentSettings basis, AgentSettingsOverrides layer) => new(
        layer.HeartbeatSeconds ?? basis.HeartbeatSeconds, layer.SettingsPollSeconds ?? basis.SettingsPollSeconds,
        layer.BatchIntervalSeconds ?? basis.BatchIntervalSeconds);
    private static AgentSettingsOverrides Values(AgentSetting? row) => new(row?.HeartbeatSeconds, row?.SettingsPollSeconds, row?.BatchIntervalSeconds);

    internal static AgentSettingsView ToView(Guid? deviceId, IReadOnlyList<AgentSetting> rows)
    {
        var platform = rows.SingleOrDefault(x => x.DeviceId is null);
        var device = deviceId is null ? null : rows.SingleOrDefault(x => x.DeviceId == deviceId);
        var inherited = deviceId is null ? AgentSettings.Default : Resolve(AgentSettings.Default, Values(platform));
        var effective = Resolve(AgentSettings.Default, Values(platform));
        if (device is not null) effective = Resolve(effective, Values(device));
        var scope = deviceId is null ? platform : device;
        return new(deviceId, Values(scope), scope is null ? "" : Convert.ToBase64String(scope.Version), AgentSettingsSnapshot.Create(effective), AgentSettingsSnapshot.Create(inherited));
    }

    internal static Task<List<AgentSetting>> ReadLayersAsync(AgentManagementDbContext db, Guid? deviceId, CancellationToken token) =>
        db.Settings.AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
}
