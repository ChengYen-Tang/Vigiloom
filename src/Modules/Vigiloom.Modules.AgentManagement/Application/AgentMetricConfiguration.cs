using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves platform and device overrides for metric collection.</summary>
public sealed class AgentMetricConfiguration(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, AgentIdentity identity, TimeProvider clock)
{
    public async Task<AgentMetricSettingsView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        if (deviceId is { } id && (id == Guid.Empty || await devices.GetAsync(id, token) is null)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.MetricSettings.AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
        var view = ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return view;
    }

    public async Task<AgentMetricSettingsSnapshot?> GetForAgentAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var rows = await db.MetricSettings.FromSqlInterpolated($"SELECT * FROM [agent].[MetricSettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {agent.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var snapshot = ToView(agent.DeviceId, rows).Effective;
        await transaction.CommitAsync(token);
        return snapshot;
    }

    public async Task<AgentMetricSettingsView?> UpdateAsync(Guid? deviceId, AgentMetricSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update?.Overrides is null || !Valid(update.Overrides))
            throw new ArgumentException("Invalid Agent metric settings update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        if (deviceId is { } id && await devices.GetAsync(id, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.MetricSettings.FromSqlInterpolated($"SELECT * FROM [agent].[MetricSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}").ToListAsync(token);
        var setting = rows.SingleOrDefault(x => x.DeviceId == deviceId);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new AgentMetricSetting { DeviceId = deviceId };
            db.MetricSettings.Add(setting);
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
        setting.UpdatedAt = clock.GetUtcNow();
        setting.UpdatedBy = actorId;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        var view = ToView(deviceId, rows);
        try
        {
            await db.SaveChangesAsync(token);
            db.MetricSettingAudits.Add(new AgentMetricSettingAudit
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
            return ToView(deviceId, rows);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }

    public static bool Valid(AgentMetricSettingsOverrides? layer) => layer is not null && Resolve(AgentMetricSettings.Default, layer).IsValid();
    private static AgentMetricSettings Resolve(AgentMetricSettings basis, AgentMetricSettingsOverrides layer) =>
        new(layer.HostIntervalSeconds ?? basis.HostIntervalSeconds, layer.ProcessIntervalSeconds ?? basis.ProcessIntervalSeconds,
            layer.CpuMillisecondsPerMinute ?? basis.CpuMillisecondsPerMinute, layer.WorkingSetLimitMiB ?? basis.WorkingSetLimitMiB,
            layer.ProcessCollectionBudgetMs ?? basis.ProcessCollectionBudgetMs,
            layer.IoLimitMiBPerMinute ?? basis.IoLimitMiBPerMinute);
    private static AgentMetricSettingsOverrides Values(AgentMetricSetting? row) => new(row?.HostIntervalSeconds, row?.ProcessIntervalSeconds, row?.CpuMillisecondsPerMinute, row?.WorkingSetLimitMiB, row?.ProcessCollectionBudgetMs, row?.IoLimitMiBPerMinute);
    internal static AgentMetricSettingsView ToView(Guid? deviceId, IReadOnlyList<AgentMetricSetting> rows)
    {
        var platform = rows.SingleOrDefault(x => x.DeviceId is null);
        var device = deviceId is null ? null : rows.SingleOrDefault(x => x.DeviceId == deviceId);
        var inherited = deviceId is null ? AgentMetricSettings.Default : Resolve(AgentMetricSettings.Default, Values(platform));
        var effective = deviceId is null ? Resolve(inherited, Values(platform)) : Resolve(inherited, Values(device));
        var scope = deviceId is null ? platform : device;
        return new(deviceId, Values(scope), scope is null ? "" : Convert.ToBase64String(scope.Version),
            AgentMetricSettingsSnapshot.Create(effective), AgentMetricSettingsSnapshot.Create(inherited));
    }
}
