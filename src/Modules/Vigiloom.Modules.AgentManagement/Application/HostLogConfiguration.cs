using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum HostLogConfigurationStatus { Success, NotFound, Conflict, LimitReached }
public sealed record HostLogConfigurationResult(HostLogConfigurationStatus Status, HostLogSourceView? Source = null);

/// <summary>Manages per-device sources and commits allow-listed audits with every effective change.</summary>
public sealed class HostLogConfiguration(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, AgentIdentity identity, TimeProvider clock)
{
    public async Task<IReadOnlyList<HostLogSourceView>?> ListAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.HostLogSources.AsNoTracking().Where(x => x.DeviceId == deviceId && x.RetiredAt == null)
            .OrderBy(x => x.RowId).ToListAsync(token);
        return rows.Select(View).ToArray();
    }

    public async Task<AgentLogSettingsSnapshot?> GetForAgentAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var rows = await db.HostLogSources.FromSqlInterpolated($"SELECT * FROM [agent].[HostLogSources] WITH (HOLDLOCK) WHERE [DeviceId] = {agent.DeviceId} AND [RetiredAt] IS NULL")
            .AsNoTracking().ToListAsync(token);
        var snapshot = AgentLogSettingsSnapshot.Create(rows.Select(x => x.ToSetting()));
        await transaction.CommitAsync(token);
        return snapshot;
    }

    public async Task<HostLogConfigurationResult> CreateAsync(Guid deviceId, HostLogSourceCreate input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (deviceId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device and actor are required.");
        var source = new HostLogSource(deviceId, input.Kind, input.Name, input.Target, input.Enabled, input.TextMode, actor, clock.GetUtcNow());
        if (await devices.GetAsync(deviceId, token) is null) return new(HostLogConfigurationStatus.NotFound);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var active = await db.HostLogSources.FromSqlInterpolated($"SELECT * FROM [agent].[HostLogSources] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] = {deviceId} AND [RetiredAt] IS NULL").ToListAsync(token);
        if (active.Count >= AgentLogSettingsSnapshot.MaximumSources) return new(HostLogConfigurationStatus.LimitReached);
        db.HostLogSources.Add(source);
        await db.SaveChangesAsync(token);
        db.HostLogSourceAudits.Add(new(source, HostLogSourceAction.Created));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(HostLogConfigurationStatus.Success, View(source));
    }

    public async Task<HostLogConfigurationResult> UpdateAsync(Guid deviceId, Guid sourceId, HostLogSourceUpdate input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (deviceId == Guid.Empty || sourceId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device, source and actor are required.");
        if (!TryVersion(input.Version, out var version)) return new(HostLogConfigurationStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var source = await db.HostLogSources.SingleOrDefaultAsync(x => x.Id == sourceId && x.DeviceId == deviceId && x.RetiredAt == null, token);
        if (source is null) return new(HostLogConfigurationStatus.NotFound);
        if (!version.AsSpan().SequenceEqual(source.Version)) return new(HostLogConfigurationStatus.Conflict);
        if (!source.Update(input.Name, input.Enabled, input.TextMode, actor, clock.GetUtcNow())) return new(HostLogConfigurationStatus.Success, View(source));
        try
        {
            await db.SaveChangesAsync(token);
            db.HostLogSourceAudits.Add(new(source, HostLogSourceAction.Updated));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(HostLogConfigurationStatus.Success, View(source));
        }
        catch (DbUpdateConcurrencyException) { return new(HostLogConfigurationStatus.Conflict); }
    }

    public async Task<HostLogConfigurationResult> RetireAsync(Guid deviceId, Guid sourceId, string versionText, Guid actor, CancellationToken token)
    {
        if (deviceId == Guid.Empty || sourceId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device, source and actor are required.");
        if (!TryVersion(versionText, out var version)) return new(HostLogConfigurationStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var source = await db.HostLogSources.SingleOrDefaultAsync(x => x.Id == sourceId && x.DeviceId == deviceId && x.RetiredAt == null, token);
        if (source is null) return new(HostLogConfigurationStatus.NotFound);
        if (!version.AsSpan().SequenceEqual(source.Version)) return new(HostLogConfigurationStatus.Conflict);
        source.Retire(actor, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(token);
            db.HostLogSourceAudits.Add(new(source, HostLogSourceAction.Retired));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(HostLogConfigurationStatus.Success, View(source));
        }
        catch (DbUpdateConcurrencyException) { return new(HostLogConfigurationStatus.Conflict); }
    }

    private static HostLogSourceView View(HostLogSource source) => new(source.ToSetting(), Convert.ToBase64String(source.Version));
    private static bool TryVersion(string? text, out byte[] version)
    {
        try { version = Convert.FromBase64String(text ?? ""); return version.Length == 8; }
        catch (FormatException) { version = []; return false; }
    }
}
