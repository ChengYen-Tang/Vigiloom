using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class ServerHostMonitoringClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    DeviceInventoryAgent ownership, InventoryQuery inventory, IHostMetricHistory metrics, IHostMetricThresholds thresholds, IHostLogHistory logs,
    IntegrityQuery integrity, IntegrityChangeQuery changes, InventoryChangeQuery inventoryChanges, SigmaDetectionHistory detections, IocDetectionHistory iocDetections, Vigiloom.Web.ServiceDetails.TelemetryRetentionAccess retention, EventTimelineAudit audit) : IHostMonitoringClient
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
    public async Task<HostMetricSeriesPage?> GetMetricSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int page, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await metrics.GetSeriesAsync(deviceId, from, until, kind, page, token);
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
        var user = await AuthorizeAsync(token);
        var page = await logs.GetAsync(deviceId, from, until, cursor, null, token);
        if (page is null) return null;
        var result = page with { RetentionDays = (await retention.GetAsync(token)).LogDays };
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) throw new UnauthorizedAccessException();
        await audit.RecordEvidenceAsync(actor, new EventEvidenceAccessed("host-log-list", deviceId, deviceId), token);
        return result;
    }
    public async Task<SigmaDetectionEventPage?> GetDetectionsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await detections.GetEventsAsync(deviceId, from, until, cursor, token);
    }
    public async Task<SigmaDetectionGapPage?> GetDetectionGapsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return await detections.GetGapsAsync(deviceId, from, until, cursor, token);
    }
    public async Task<IocDetectionPage?> GetIocDetectionsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, int page, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var result = await iocDetections.GetAsync(deviceId, from, until, page, token);
        return result is null ? null : result with { RetentionDays = (await retention.GetAsync(token)).LogDays };
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
    private async Task<ClaimsPrincipal> AuthorizeAsync(CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
        return state.User;
    }
}
