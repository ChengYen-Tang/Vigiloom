using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Contracts.Agent;

/// <summary>Private, bounded host monitoring reads used by the device details UI.</summary>
public interface IHostMonitoringClient
{
    Task<DeviceInventoryView?> GetInventoryAsync(Guid deviceId, CancellationToken token);
    Task<InventoryChangePage?> GetInventoryChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, InventorySource? source, string? cursor, CancellationToken token);
    Task<InventorySnapshotChunkView?> GetInventoryChunkAsync(Guid deviceId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token);
    Task<HostMetricPage?> GetMetricsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token);
    Task<HostMetricAggregate?> AggregateMetricsAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token);
    Task<HostLogPage?> GetLogsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token);
    Task<DeviceIntegrityView?> GetIntegrityAsync(Guid deviceId, CancellationToken token);
    Task<IntegritySnapshotChunkView?> GetIntegrityChunkAsync(Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, CancellationToken token);
    Task<IntegrityChangePage?> GetIntegrityChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token);
}
