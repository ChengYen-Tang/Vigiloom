using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Persists Agent setting changes and their audit in the owning database.</summary>
internal static class AgentTransportSettingsWriter
{
    internal static async Task<AgentSettingsView?> UpdateAsync(DbContext db, Guid? deviceId, AgentSettingsUpdate update,
        Guid actorId, DateTimeOffset now, byte[] version, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        // Update locks serialize competing edits, including the missing platform or device row.
        var rows = await db.Set<AgentSetting>().FromSqlInterpolated($"SELECT * FROM [agent].[Settings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}")
            .ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new AgentSetting { DeviceId = deviceId };
            db.Set<AgentSetting>().Add(setting);
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
        setting.UpdatedAt = now;
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var view = AgentConfiguration.ToView(deviceId, rows);
        try
        {
            await db.SaveChangesAsync(token);
            db.Set<AgentSettingAudit>().Add(new AgentSettingAudit
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
            return AgentConfiguration.ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }
}
