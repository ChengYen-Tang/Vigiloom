using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class ServerHostMonitoringClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    DeviceInventoryAgent ownership, InventoryQuery inventory, HostMetricHistory metrics, HostMetricThresholds thresholds, HostLogHistory logs,
    IntegrityQuery integrity, IntegrityChangeQuery changes, InventoryChangeQuery inventoryChanges) : IHostMonitoringClient
{
    public async Task<DeviceInventoryView?> GetInventoryAsync(Guid deviceId, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists ? await inventory.GetAsync(deviceId, resolved.AgentId, token) : null;
    }
    public async Task<InventoryChangePage?> GetInventoryChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, InventorySource? source, string? cursor, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists ? await inventoryChanges.GetAsync(deviceId, from, until, source, cursor, token) : null;
    }
    public async Task<InventorySnapshotChunkView?> GetInventoryChunkAsync(Guid deviceId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists && resolved.AgentId is { } agent ? await inventory.GetChunkAsync(deviceId, agent, source, snapshotId, chunkIndex, token) : null;
    }
    public async Task<HostMetricPage?> GetMetricsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await metrics.GetAsync(deviceId, from, until, cursor, kind, token);
    }
    public async Task<HostMetricThresholdStatus?> GetMetricThresholdAsync(Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await thresholds.GetAsync(deviceId, agentId, kind, source, dimension, token);
    }
    public async Task<HostMetricAggregate?> AggregateMetricsAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await metrics.AggregateAsync(deviceId, agentId, from, until, kind, source, dimension, bucketSeconds, token);
    }
    public async Task<HostLogPage?> GetLogsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await logs.GetAsync(deviceId, from, until, cursor, null, token);
    }
    public async Task<DeviceIntegrityView?> GetIntegrityAsync(Guid deviceId, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists ? await integrity.GetAsync(deviceId, resolved.AgentId, token) : null;
    }
    public async Task<IntegritySnapshotChunkView?> GetIntegrityChunkAsync(Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists && resolved.AgentId is { } agent ? await integrity.GetChunkAsync(deviceId, agent, scopeId, snapshotId, chunkIndex, token) : null;
    }
    public async Task<IntegrityChangePage?> GetIntegrityChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var resolved = await ownership.ResolveAsync(deviceId, token);
        return resolved.Exists ? await changes.GetAsync(deviceId, from, until, null, cursor, token) : null;
    }
    private async Task AuthorizeAsync(CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
    }
}
