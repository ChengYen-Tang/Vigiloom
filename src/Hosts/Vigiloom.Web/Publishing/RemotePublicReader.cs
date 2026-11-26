using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Combines resource-owned public policies with the current monitoring state.</summary>
internal sealed class RemotePublicReader(HttpClient client, PingStore ping, HealthStatusReader health,
    IDbContextFactory<ServiceObservabilityDbContext> observations, IConfiguration configuration, IServiceProvider provider)
{
    private RemoteNetworkPublicClient? Network => string.IsNullOrWhiteSpace(configuration["Services:Network:Url"]) ? null : provider.GetRequiredService<RemoteNetworkPublicClient>();
    private const string Root = "internal/v1/publications/";

    public Task<PublishedDevicePolicy?> DevicePolicyAsync(Guid id, CancellationToken token) => GetAsync<PublishedDevicePolicy>($"devices/{id:D}", token);
    public Task<PublishedServicePolicy?> ServicePolicyAsync(Guid id, CancellationToken token) => GetAsync<PublishedServicePolicy>($"services/{id:D}", token);
    private Task<PublishedGroupSummary?> GroupSummaryAsync(Guid id, CancellationToken token) => GetAsync<PublishedGroupSummary>($"groups/{id:D}/summary", token);
    private Task<PublishedGroupPolicy?> GroupPolicyAsync(Guid id, CancellationToken token) => GetAsync<PublishedGroupPolicy>($"groups/{id:D}", token);

    public async Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken token)
    {
        var first = await PageAsync<PublishedGroupSummary>("groups", search, page, null, token);
        var current = await Task.WhenAll(first.Items.Select(x => GroupSummaryAsync(x.Id, token)));
        var items = first.Items.Zip(current).Where(x => x.Second is not null && x.First.Version == x.Second.Version)
            .Select(x => Map(x.Second!)).ToArray();
        return new(items, page, first.HasMore);
    }

    public async Task<PublicGroup?> GroupAsync(Guid id, CancellationToken token)
    {
        var first = await GroupSummaryAsync(id, token);
        if (first is null) return null;
        var current = await GroupSummaryAsync(id, token);
        return current is not null && first.Version == current.Version ? Map(current) : null;
    }

    public async Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        var group = groupId is { } id ? await GroupPolicyAsync(id, token) : null;
        if (groupId is not null && group is null) return new([], page, false);
        var first = await PageAsync<PublishedDevicePolicy>("devices", search, page, groupId, token);
        if (Network is { } network)
        {
            var statuses = (await network.DevicesAsync(first.Items.Select(item => item.Id).ToArray(), token)).ToDictionary(item => item.Id);
            var updated = await PageAsync<PublishedDevicePolicy>("devices", search, page, groupId, token);
            if (groupId is { } selectedGroup && group != await GroupPolicyAsync(selectedGroup, token)) return new([], page, false);
            var currentPolicies = updated.Items.ToDictionary(item => item.Id);
            var visible = first.Items.Where(item => statuses.ContainsKey(item.Id) && currentPolicies.TryGetValue(item.Id, out var current) && item == current)
                .Select(item => statuses[item.Id]).ToArray();
            return new(visible, page, first.HasMore);
        }
        var status = await ReadDevicesAsync(first.Items, token);
        var current = await PageAsync<PublishedDevicePolicy>("devices", search, page, groupId, token);
        if (groupId is { } groupKey && group != await GroupPolicyAsync(groupKey, token)) return new([], page, false);
        var latest = current.Items.ToDictionary(x => x.Id);
        var items = first.Items.Where(x => latest.TryGetValue(x.Id, out var item) && x == item)
            .Select(x => Map(x, status)).ToArray();
        return new(items, page, first.HasMore);
    }

    public async Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken token)
    {
        var first = await DevicePolicyAsync(id, token);
        if (first is null) return null;
        if (Network is { } network)
        {
            var result = await network.DeviceAsync(id, token);
            return first == await DevicePolicyAsync(id, token) ? result : null;
        }
        var status = await ReadDevicesAsync([first], token);
        return first == await DevicePolicyAsync(id, token) ? Map(first, status) : null;
    }

    public async Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        var group = groupId is { } id ? await GroupPolicyAsync(id, token) : null;
        if (groupId is not null && group is null) return new([], page, false);
        var first = await PageAsync<PublishedServicePolicy>("services", search, page, groupId, token);
        var versions = await SourceVersionsAsync(first.Items.Select(x => x.Id).ToArray(), token);
        var states = await health.ReadManyAsync(first.Items.Select(x => x.Id).ToArray(), token);
        var currentVersions = await SourceVersionsAsync(first.Items.Select(x => x.Id).ToArray(), token);
        var current = await PageAsync<PublishedServicePolicy>("services", search, page, groupId, token);
        if (groupId is { } groupKey && group != await GroupPolicyAsync(groupKey, token)) return new([], page, false);
        var latest = current.Items.ToDictionary(x => x.Id);
        var items = first.Items.Where(x => latest.TryGetValue(x.Id, out var item) && x == item && states.ContainsKey(x.Id) &&
                versions.TryGetValue(x.Id, out var before) && currentVersions.TryGetValue(x.Id, out var after) && Same(before, after))
            .Select(x => Map(x, states[x.Id])).ToArray();
        return new(items, page, first.HasMore);
    }

    public async Task<PublicService?> ServiceAsync(Guid id, CancellationToken token)
    {
        var first = await ServicePolicyAsync(id, token);
        if (first is null) return null;
        var versions = await SourceVersionsAsync([id], token);
        var states = await health.ReadManyAsync([id], token);
        var current = await SourceVersionsAsync([id], token);
        return first == await ServicePolicyAsync(id, token) && states.TryGetValue(id, out var state) && versions.TryGetValue(id, out var before) &&
            current.TryGetValue(id, out var after) && Same(before, after) ? Map(first, state) : null;
    }

    private async Task<Dictionary<Guid, SourceVersions>> SourceVersionsAsync(Guid[] ids, CancellationToken token)
    {
        await using var db = await observations.CreateDbContextAsync(token);
        return await (from service in db.Services.AsNoTracking()
            join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
            where ids.Contains(service.Id)
            select new SourceVersions(service.Id, service.Version, source.Version)).ToDictionaryAsync(x => x.Id, token);
    }

    private static bool Same(SourceVersions before, SourceVersions after) => before.Service.AsSpan().SequenceEqual(after.Service) &&
        before.Source.AsSpan().SequenceEqual(after.Source);
    private sealed record SourceVersions(Guid Id, byte[] Service, byte[] Source);

    private Task<IReadOnlyDictionary<Guid, DevicePingStatus>> ReadDevicesAsync(IReadOnlyList<PublishedDevicePolicy> devices, CancellationToken token) =>
        ping.GetStatusesAsync(devices.Where(x => x.Enabled)
            .Select(x => new PingDeviceSnapshot(x.Id, Convert.FromBase64String(x.Version), [x.Target])).ToArray(), token);

    private static PublicDevice Map(PublishedDevicePolicy row, IReadOnlyDictionary<Guid, DevicePingStatus> states)
    {
        if (!row.Enabled || !states.TryGetValue(row.Id, out var status)) return new(row.Id, row.Name, "Unknown", null, null);
        var target = status.Targets[0];
        var state = target.State switch { Reachability.Reachable => "Operational", Reachability.Unreachable => "Unavailable", _ => "Unknown" };
        var latency = target.State == Reachability.Reachable && target.LastOutcome == PingOutcome.Success && !target.Stale ? target.LatencyMilliseconds : null;
        return new(row.Id, row.Name, state, row.ShowLatency ? latency : null, row.ShowObservedAt ? target.MeasuredAt : null);
    }

    private static PublicService Map(PublishedServicePolicy row, HealthStatusReader.OverviewStatus status) =>
        new(row.Id, row.Name, status.CollectionStopped || status.SourceRevoked ? "Unknown" : status.Status.State switch
        { ServiceHealthState.Healthy => "Healthy", ServiceHealthState.Degraded => "Degraded", ServiceHealthState.Unhealthy => "Unhealthy", _ => "Unknown" });

    private static PublicGroup Map(PublishedGroupSummary row) => new(row.Id, row.Name, row.DeviceCount, row.ServiceCount);

    private async Task<PublicPage<T>> PageAsync<T>(string kind, string? search, int page, Guid? groupId, CancellationToken token)
    {
        var url = QueryHelpers.AddQueryString(Root + kind, new Dictionary<string, string?>
        { ["search"] = search, ["page"] = page.ToString(CultureInfo.InvariantCulture), ["groupId"] = groupId?.ToString("D") });
        return await client.GetFromJsonAsync<PublicPage<T>>(url, token) ?? throw new InvalidOperationException("Resource service returned an empty catalog response.");
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(Root + path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new InvalidOperationException("Resource service returned an empty policy response.");
    }
}
