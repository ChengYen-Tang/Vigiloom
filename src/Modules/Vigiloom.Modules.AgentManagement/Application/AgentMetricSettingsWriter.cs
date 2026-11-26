using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Persists Agent metric settings and audit in the owning database.</summary>
internal static class AgentMetricSettingsWriter
{
    internal static async Task<AgentMetricSettingsView?> UpdateAsync(DbContext db, Guid? deviceId, AgentMetricSettingsUpdate update,
        Guid actorId, DateTimeOffset now, byte[] version, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.Set<AgentMetricSetting>().FromSqlInterpolated($"SELECT * FROM [agent].[MetricSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}").ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new AgentMetricSetting { DeviceId = deviceId };
            db.Set<AgentMetricSetting>().Add(setting);
            rows.Add(setting);
        }
        else
        {
            if (version.Length != 8 || !version.AsSpan().SequenceEqual(setting.Version)) return null;
            db.Entry(setting).Property(x => x.Version).OriginalValue = version;
        }
        setting.HostIntervalSeconds = update.Overrides.HostIntervalSeconds;
        setting.ProcessIntervalSeconds = update.Overrides.ProcessIntervalSeconds;
        setting.CpuMillisecondsPerMinute = update.Overrides.CpuMillisecondsPerMinute;
        setting.WorkingSetLimitMiB = update.Overrides.WorkingSetLimitMiB;
        setting.ProcessCollectionBudgetMs = update.Overrides.ProcessCollectionBudgetMs;
        setting.IoLimitMiBPerMinute = update.Overrides.IoLimitMiBPerMinute;
        setting.UpdatedAt = now;
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var view = AgentMetricConfiguration.ToView(deviceId, rows);
        try
        {
            await db.SaveChangesAsync(token);
            db.Set<AgentMetricSettingAudit>().Add(new AgentMetricSettingAudit
            {
                SettingId = setting.Id, DeviceId = deviceId, ActorId = actorId, OccurredAt = setting.UpdatedAt,
                HostIntervalSeconds = setting.HostIntervalSeconds, ProcessIntervalSeconds = setting.ProcessIntervalSeconds,
                CpuMillisecondsPerMinute = setting.CpuMillisecondsPerMinute,
                WorkingSetLimitMiB = setting.WorkingSetLimitMiB,
                ProcessCollectionBudgetMs = setting.ProcessCollectionBudgetMs,
                IoLimitMiBPerMinute = setting.IoLimitMiBPerMinute,
                EffectiveHostIntervalSeconds = view.Effective.Settings.HostIntervalSeconds,
                EffectiveProcessIntervalSeconds = view.Effective.Settings.ProcessIntervalSeconds,
                EffectiveCpuMillisecondsPerMinute = view.Effective.Settings.CpuMillisecondsPerMinute,
                EffectiveWorkingSetLimitMiB = view.Effective.Settings.WorkingSetLimitMiB,
                EffectiveProcessCollectionBudgetMs = view.Effective.Settings.ProcessCollectionBudgetMs,
                EffectiveIoLimitMiBPerMinute = view.Effective.Settings.IoLimitMiBPerMinute,
                EffectiveRevision = view.Effective.Revision, ChangeVersion = setting.Version.ToArray()
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return AgentMetricConfiguration.ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }
}
