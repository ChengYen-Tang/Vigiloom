using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Resolves and audits the independent Agent queue capacity group.</summary>
public sealed class AgentQueueCapacityConfiguration(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, AgentIdentity identity, TimeProvider clock) : IAgentQueueCapacitySettings
{
    public async Task<AgentQueueCapacityView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        if (deviceId is { } id && (id == Guid.Empty || await devices.GetAsync(id, token) is null)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var rows = await db.QueueCapacitySettings.AsNoTracking().Where(x => x.DeviceId == null || x.DeviceId == deviceId).ToListAsync(token);
        var view = ToView(deviceId, rows);
        await transaction.CommitAsync(token);
        return view;
    }

    public async Task<AgentQueueCapacitySnapshot?> GetForAgentAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return null;
        var rows = await db.QueueCapacitySettings.FromSqlInterpolated($"SELECT * FROM [agent].[QueueCapacitySettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {agent.DeviceId}")
            .AsNoTracking().ToListAsync(token);
        var snapshot = ToView(agent.DeviceId, rows).Effective;
        await transaction.CommitAsync(token);
        return snapshot;
    }

    public async Task<AgentQueueCapacityView?> UpdateAsync(Guid? deviceId, AgentQueueCapacityUpdate update, Guid actorId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty || update?.Overrides is null || !Valid(update.Overrides))
            throw new ArgumentException("Invalid Agent queue capacity update.", nameof(update));
        byte[] version;
        try { version = Convert.FromBase64String(update.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        if (deviceId is { } id && await devices.GetAsync(id, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await AgentQueueCapacityWriter.UpdateAsync(db, deviceId, update, actorId, clock.GetUtcNow(), version, token);
    }

    public static bool Valid(AgentQueueCapacityOverrides? layer) => layer is not null && Resolve(AgentQueueCapacity.Default, layer).IsValid();
    private static AgentQueueCapacity Resolve(AgentQueueCapacity basis, AgentQueueCapacityOverrides layer) =>
        new(layer.PayloadBytes ?? basis.PayloadBytes, layer.BatchCount ?? basis.BatchCount);
    private static AgentQueueCapacityOverrides Values(AgentQueueCapacitySetting? row) => new(row?.PayloadBytes, row?.BatchCount);
    internal static AgentQueueCapacityView ToView(Guid? deviceId, IReadOnlyList<AgentQueueCapacitySetting> rows)
    {
        var platform = rows.SingleOrDefault(x => x.DeviceId is null);
        var device = deviceId is null ? null : rows.SingleOrDefault(x => x.DeviceId == deviceId);
        var inherited = deviceId is null ? AgentQueueCapacity.Default : Resolve(AgentQueueCapacity.Default, Values(platform));
        var effective = device is null ? inherited : Resolve(inherited, Values(device));
        var scope = deviceId is null ? platform : device;
        if (deviceId is null) effective = Resolve(AgentQueueCapacity.Default, Values(platform));
        return new(deviceId, Values(scope), scope is null ? "" : Convert.ToBase64String(scope.Version),
            AgentQueueCapacitySnapshot.Create(effective), AgentQueueCapacitySnapshot.Create(inherited));
    }
}
