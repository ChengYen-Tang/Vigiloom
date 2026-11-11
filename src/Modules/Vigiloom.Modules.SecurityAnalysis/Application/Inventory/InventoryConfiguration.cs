using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Platform and per-device inventory policy with transactional audit.</summary>
public sealed class InventoryConfiguration(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<InventorySettingsView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        if (deviceId is { } id && (id == Guid.Empty || await devices.GetAsync(id, token) is null)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.InventorySettings.AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
        var view = ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return view;
    }

    public async Task<InventorySettingsView?> UpdateAsync(Guid? deviceId, InventorySettingsUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update?.Overrides is null || !Valid(update.Overrides))
            throw new ArgumentException("Invalid inventory settings update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        if (deviceId is { } id && await devices.GetAsync(id, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.InventorySettings.FromSqlInterpolated($"SELECT * FROM [security].[InventorySettings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}").ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new InventorySetting { DeviceId = deviceId };
            db.InventorySettings.Add(setting);
            rows.Add(setting);
        }
        else
        {
            if (version.Length != 8 || !version.AsSpan().SequenceEqual(setting.Version)) return null;
            db.Entry(setting).Property(x => x.Version).OriginalValue = version;
        }
        setting.ScanIntervalSeconds = update.Overrides.ScanIntervalSeconds;
        setting.UpdatedAt = clock.GetUtcNow();
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var effective = ToView(deviceId, rows).Effective;
        try
        {
            await db.SaveChangesAsync(token);
            db.InventorySettingAudits.Add(new InventorySettingAudit
            {
                SettingId = setting.Id, DeviceId = deviceId, ActorId = actorId, OccurredAt = setting.UpdatedAt,
                ScanIntervalSeconds = setting.ScanIntervalSeconds,
                EffectiveScanIntervalSeconds = effective.Settings.ScanIntervalSeconds,
                EffectiveRevision = effective.Revision, ChangeVersion = setting.Version.ToArray()
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }

    public static bool Valid(InventorySettingsOverrides? layer) => layer is not null &&
        new InventorySettings(layer.ScanIntervalSeconds ?? InventorySettings.Default.ScanIntervalSeconds).IsValid();

    internal static InventorySettingsView ToView(Guid? deviceId, IReadOnlyList<InventorySetting> rows)
    {
        var platform = rows.SingleOrDefault(x => x.DeviceId is null);
        var device = deviceId is null ? null : rows.SingleOrDefault(x => x.DeviceId == deviceId);
        var inherited = deviceId is null ? InventorySettings.Default :
            new InventorySettings(platform?.ScanIntervalSeconds ?? InventorySettings.Default.ScanIntervalSeconds);
        var scope = deviceId is null ? platform : device;
        var effective = new InventorySettings(scope?.ScanIntervalSeconds ?? inherited.ScanIntervalSeconds);
        return new(deviceId, new(scope?.ScanIntervalSeconds), scope is null ? "" : Convert.ToBase64String(scope.Version),
            InventorySettingsSnapshot.Create(effective), InventorySettingsSnapshot.Create(inherited));
    }
}
