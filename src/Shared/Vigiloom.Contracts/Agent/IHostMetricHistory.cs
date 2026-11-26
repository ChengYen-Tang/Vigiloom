namespace Vigiloom.Contracts.Agent;

public interface IHostMetricHistory
{
    Task<HostMetricPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token);
    Task<HostMetricSeriesPage?> GetSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int page, CancellationToken token);
    Task<HostMetricAggregate?> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until,
        AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token);
}
