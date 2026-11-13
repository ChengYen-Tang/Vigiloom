using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

/// <summary>Authorized callers query raw samples and five-minute buckets with the same target, location and UTC range.</summary>
public sealed class PingHistoryReader(IDbContextFactory<NetworkDbContext> contexts)
{
    public const int PageSize = 50;
    public const int MaximumOptions = 100;
    public static bool Valid(PingHistoryQuery q) => q.DeviceId.Version == 7 && q.From.Offset == TimeSpan.Zero && q.To.Offset == TimeSpan.Zero &&
        q.To > q.From && q.To - q.From <= TimeSpan.FromDays(7) &&
        q.From.Ticks % TimeSpan.FromMinutes(5).Ticks == 0 && q.To.Ticks % TimeSpan.FromMinutes(5).Ticks == 0 &&
        q.Page is >= 1 and <= 10000 && q.Target?.Length is not > 253 && !string.IsNullOrWhiteSpace(q.Location) && q.Location.Length <= 256;

    public async Task<PingHistoryPage> QueryAsync(PingHistoryQuery query, CancellationToken token)
    {
        if (!Valid(query)) throw new ArgumentException("Use a valid device and five-minute-aligned UTC range of at most seven days.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var samples = db.PingSamples.AsNoTracking().Where(x => x.DeviceId == query.DeviceId && x.OccurredAt >= query.From && x.OccurredAt < query.To);
        var windows = db.PingRollups.AsNoTracking().Where(x => x.DeviceId == query.DeviceId && x.WindowStart >= query.From && x.WindowStart < query.To);
        var options = await samples.Select(x => new { x.Target, x.Location })
            .Union(windows.Select(x => new { x.Target, x.Location }))
            .OrderBy(x => x.Target).ThenBy(x => x.Location).Take(MaximumOptions + 1).ToArrayAsync(token);
        var selected = samples.Where(x => x.Target == query.Target && x.Location == query.Location);
        var selectedWindows = windows.Where(x => x.Target == query.Target && x.Location == query.Location);
        var count = await selected.LongCountAsync(token);
        var pending = await selected.AnyAsync(x => !x.Aggregated, token);
        var latestRaw = await selected.MaxAsync(x => (DateTimeOffset?)x.OccurredAt, token);
        var ordered = query.Descending ? selected.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            : selected.OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId);
        var rows = await ordered.Skip((query.Page - 1) * PageSize).Take(PageSize)
            .Select(x => new PingHistorySample(x.OccurredAt, x.ReceivedAt, x.Outcome, x.Succeeded, x.LatencyMilliseconds, x.ResolvedAddress)).ToArrayAsync(token);
        var buckets = await selectedWindows.OrderBy(x => x.WindowStart)
            .Select(x => new PingHistoryBucket(x.WindowStart, x.Attempts, x.Successes, x.Timeouts, x.Unreachable, x.DnsErrors,
                x.PermissionDenied, x.MonitorErrors, x.UnknownFailures, x.Successes == 0 || x.LatencyOverflow ? null : x.LatencySum / x.Successes,
                x.LatencyMin, x.LatencyMax, x.LatencyOverflow, x.LastOccurredAt)).ToArrayAsync(token);
        var latestBucket = buckets.Max(x => (DateTimeOffset?)x.LastOccurredAt);
        var latest = latestRaw is null ? latestBucket : latestBucket is null || latestRaw > latestBucket ? latestRaw : latestBucket;
        return new(options.Take(MaximumOptions).Select(x => new PingHistoryOption(x.Target, x.Location)).ToArray(), options.Length > MaximumOptions, rows, query.Page, PageSize, count, buckets, pending, latest);
    }
}
