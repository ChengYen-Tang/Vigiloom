using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Network;

/// <summary>Projects only fields currently allowed by the resource service's publication policy.</summary>
internal sealed class PublicPing(HttpClient resources, PingStore ping, IDbContextFactory<NetworkDbContext> contexts)
{
    public async Task<PublicDevice?> ReadAsync(Guid id, CancellationToken token)
    {
        var policy = await PolicyAsync(id, token);
        if (policy is null) return null;
        DevicePingStatus? status = policy.Enabled ? await ping.GetStatusAsync(id, [policy.Target], policy.Version, token) : null;
        if (policy != await PolicyAsync(id, token)) return null;
        if (status is null) return new(id, policy.Name, "Unknown", null, null);
        var target = status.Targets[0];
        var state = target.State switch { Reachability.Reachable => "Operational", Reachability.Unreachable => "Unavailable", _ => "Unknown" };
        var latency = target.State == Reachability.Reachable && target.LastOutcome == PingOutcome.Success && !target.Stale ? target.LatencyMilliseconds : null;
        return new(id, policy.Name, state, policy.ShowLatency ? latency : null, policy.ShowObservedAt ? target.MeasuredAt : null);
    }

    public async Task<PublicDeviceHistory?> HistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        var policy = await PolicyAsync(id, token);
        if (policy?.PublicSince is not { } publicSince || policy.HistorySince is not { } historySince || policy.HistoryDays is < 1 or > 7) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var start = new[] { from, publicSince, historySince, now.AddDays(-policy.HistoryDays) }.Max();
        var end = to < now ? to : now;
        var bucket = TimeSpan.FromMinutes(5);
        var remainder = start.UtcTicks % bucket.Ticks;
        if (remainder != 0) start = start.AddTicks(bucket.Ticks - remainder);
        end = end.AddTicks(-(end.UtcTicks % bucket.Ticks));
        var points = start >= end ? [] : await db.PingRollups.AsNoTracking()
            .Where(item => item.DeviceId == id && item.Target == policy.Target && item.Location == "central" && item.WindowStart >= start && item.WindowStart < end)
            .OrderBy(item => item.WindowStart)
            .Select(item => new PublicDeviceHistoryPoint(item.WindowStart, item.Successes == 0 || item.LatencyOverflow ? null : item.LatencySum / item.Successes))
            .ToArrayAsync(token);
        return policy == await PolicyAsync(id, token) ? new(points, now) : null;
    }

    private async Task<PublishedDevicePolicy?> PolicyAsync(Guid id, CancellationToken token)
    {
        using var response = await resources.GetAsync($"internal/v1/publications/devices/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PublishedDevicePolicy>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty publication policy.");
    }
}
