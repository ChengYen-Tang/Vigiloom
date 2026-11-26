using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads retained metrics from the independent host database.</summary>
public sealed class HostMetricReader(IDbContextFactory<HostMonitoringDbContext> contexts)
{
    public async Task<HostMetricPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.InboxStates.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), true)
            .GetAsync(deviceId, from, until, cursor, kind, token);
    }

    public async Task<HostMetricSeriesPage?> GetSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int page, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.InboxStates.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), true)
            .GetSeriesAsync(deviceId, from, until, kind, page, token);
    }

    public async Task<HostMetricAggregate?> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until,
        AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await new HostMetricHistoryCore(db, db.InboxStates.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id), true)
            .AggregateAsync(deviceId, agentId, from, until, kind, source, dimension, bucketSeconds, token);
    }
}
