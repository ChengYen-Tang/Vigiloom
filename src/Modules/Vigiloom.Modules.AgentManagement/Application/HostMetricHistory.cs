using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads a stable page of host metrics for a device's Agents.</summary>
public sealed class HostMetricHistory(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices) : IHostMetricHistory
{
    public const int PageSize = HostMetricHistoryCore.PageSize;

    public async Task<HostMetricAggregate?> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token)
    {
        var exists = await devices.GetAsync(deviceId, token) is not null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), exists)
            .AggregateAsync(deviceId, agentId, from, until, kind, source, dimension, bucketSeconds, token);
    }

    public async Task<HostMetricSeriesPage?> GetSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int page, CancellationToken token)
    {
        var exists = await devices.GetAsync(deviceId, token) is not null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), exists)
            .GetSeriesAsync(deviceId, from, until, kind, page, token);
    }

    public async Task<HostMetricPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token)
    {
        var exists = await devices.GetAsync(deviceId, token) is not null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), exists)
            .GetAsync(deviceId, from, until, cursor, kind, token);
    }
}
