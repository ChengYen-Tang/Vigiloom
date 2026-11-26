using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Persists Agent queue settings and their audit in the owning database.</summary>
internal static class AgentQueueCapacityWriter
{
    internal static async Task<AgentQueueCapacityView?> UpdateAsync(DbContext db, Guid? deviceId, AgentQueueCapacityUpdate update,
        Guid actorId, DateTimeOffset now, byte[] version, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.Set<AgentQueueCapacitySetting>().FromSqlInterpolated($"SELECT * FROM [agent].[QueueCapacitySettings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}").ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new AgentQueueCapacitySetting { DeviceId = deviceId };
            db.Set<AgentQueueCapacitySetting>().Add(setting);
            rows.Add(setting);
        }
        else
        {
            if (version.Length != 8 || !version.AsSpan().SequenceEqual(setting.Version)) return null;
            db.Entry(setting).Property(x => x.Version).OriginalValue = version;
        }
        setting.PayloadBytes = update.Overrides.PayloadBytes;
        setting.BatchCount = update.Overrides.BatchCount;
        setting.UpdatedAt = now;
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var view = AgentQueueCapacityConfiguration.ToView(deviceId, rows);
        try
        {
            await db.SaveChangesAsync(token);
            db.Set<AgentQueueCapacityAudit>().Add(new AgentQueueCapacityAudit
            {
                SettingId = setting.Id, DeviceId = deviceId, ActorId = actorId, OccurredAt = setting.UpdatedAt,
                PayloadBytes = setting.PayloadBytes, BatchCount = setting.BatchCount,
                EffectivePayloadBytes = view.Effective.Capacity.PayloadBytes, EffectiveBatchCount = view.Effective.Capacity.BatchCount,
                EffectiveRevision = view.Effective.Revision, ChangeVersion = setting.Version.ToArray()
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return AgentQueueCapacityConfiguration.ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }
}
