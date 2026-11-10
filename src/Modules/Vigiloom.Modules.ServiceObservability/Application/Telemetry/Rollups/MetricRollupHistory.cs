using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

public enum MetricRollupReadStatus { Found, NotFound, Changed, UnsupportedVersion }
public sealed record MetricRollupSeriesQuery(MetricRollupReadStatus Status, MetricRollupSeriesPage? Page);
public sealed record MetricRollupPayloadQuery(MetricRollupReadStatus Status, byte[]? Payload);

/// <summary>Lists internal window states without exposing row identifiers or telemetry payloads.</summary>
internal sealed class MetricRollupHistory(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal const int PageSize = 100;
    internal const int SeriesPageSize = 50;
    internal const int MaximumSeriesPage = 200;
    private static readonly MetricRollupQuality[] KnownQuality = Enum.GetValues<MetricRollupQuality>()
        .Where(flag => flag != MetricRollupQuality.None).ToArray();
    private static readonly long KnownQualityMask = KnownQuality.Aggregate(0L, (mask, flag) => mask | (long)flag);

    internal async Task<MetricRollupWindowPage> WindowsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset? before, CancellationToken token)
    {
        if (serviceId.Version != 7 || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero ||
            to <= from || to - from > TimeSpan.FromDays(366) ||
            before is { } cursor && (cursor.Offset != TimeSpan.Zero || cursor < from || cursor > to))
            throw new ArgumentException("A service, UTC range up to 366 days, and in-range UTC cursor are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var limit = before ?? to;
        var rows = await db.MetricRollupWindows.AsNoTracking()
            .Where(x => x.ServiceId == serviceId && x.WindowStart >= from && x.WindowStart < to && x.WindowStart < limit)
            .OrderByDescending(x => x.WindowStart)
            .Select(x => new { x.WindowStart, x.InputRevision, x.ProjectedRevision, x.ProjectionVersion, x.ProjectedAt, x.IsComplete, x.RawPruned, x.Version })
            .Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Take(PageSize).Select(x => new MetricRollupWindowResponse(x.WindowStart, x.WindowStart.AddMinutes(5),
            x.ProjectedRevision is null ? "Pending" :
            x.ProjectionVersion != MetricRollupEncoding.Version ? "UnsupportedVersion" :
            x.ProjectedRevision != x.InputRevision ? "Stale" : x.IsComplete ? "Current" : "Incomplete",
            x.ProjectedAt, x.ProjectedRevision is not null, x.ProjectedRevision is not null && x.ProjectedRevision != x.InputRevision,
            x.IsComplete, x.ProjectionVersion is null || x.ProjectionVersion == MetricRollupEncoding.Version, x.RawPruned,
            Convert.ToBase64String(x.Version))).ToArray();
        return new(items, rows.Length > PageSize ? items[^1].WindowStart : null);
    }

    internal async Task<MetricRollupSeriesQuery> SeriesAsync(Guid serviceId, DateTimeOffset start, string version,
        int page, Guid? instanceId, CancellationToken token)
    {
        var versionBytes = ValidateSeriesQuery(serviceId, start, version, page, instanceId);
        await using var db = await contexts.CreateDbContextAsync(token);
        var state = await db.MetricRollupWindows.AsNoTracking().Where(x => x.ServiceId == serviceId && x.WindowStart == start)
            .Select(x => new { x.RowId, x.Version, x.ProjectionVersion }).SingleOrDefaultAsync(token);
        if (state is null) return new(MetricRollupReadStatus.NotFound, null);
        if (!state.Version.AsSpan().SequenceEqual(versionBytes)) return new(MetricRollupReadStatus.Changed, null);
        if (state.ProjectionVersion is { } projection && projection != MetricRollupEncoding.Version)
            return new(MetricRollupReadStatus.UnsupportedVersion, null);
        var query = db.MetricRollupSeries.AsNoTracking().Where(x => x.WindowRowId == state.RowId && x.ServiceId == serviceId);
        if (instanceId is { } instance) query = query.Where(x => x.InstanceId == instance);
        var rows = await query.OrderBy(x => x.RowId).Skip((page - 1) * SeriesPageSize).Take(SeriesPageSize + 1)
            .Select(x => new { x.InstanceId, x.Mode, x.QualityFlags, x.RequiresRaw, x.ResultVersion, x.InputCount,
                PayloadBytes = x.ResultPayload == null ? (int?)null : x.ResultPayload.Length }).ToArrayAsync(token);
        var current = await db.MetricRollupWindows.AsNoTracking().Where(x => x.ServiceId == serviceId && x.WindowStart == start)
            .Select(x => x.Version).SingleOrDefaultAsync(token);
        if (current is null || !current.AsSpan().SequenceEqual(versionBytes)) return new(MetricRollupReadStatus.Changed, null);
        var values = rows.Take(SeriesPageSize).Select(x => new MetricRollupSeriesResponse(x.InstanceId, x.Mode.ToString(), x.QualityFlags,
            DescribeQuality(x.QualityFlags), x.RequiresRaw, x.ResultVersion, x.InputCount, x.PayloadBytes)).ToArray();
        return new(MetricRollupReadStatus.Found, new(version, page, rows.Length > SeriesPageSize && page < MaximumSeriesPage,
            rows.Length > SeriesPageSize && page == MaximumSeriesPage, values));
    }

    internal async Task<MetricRollupPayloadQuery> PayloadAsync(Guid serviceId, DateTimeOffset start, string version,
        int page, int position, Guid? instanceId, CancellationToken token)
    {
        var versionBytes = ValidateSeriesQuery(serviceId, start, version, page, instanceId);
        if (position is < 0 or >= SeriesPageSize) throw new ArgumentException("Invalid series position.", nameof(position));
        await using var db = await contexts.CreateDbContextAsync(token);
        var state = await db.MetricRollupWindows.AsNoTracking().Where(x => x.ServiceId == serviceId && x.WindowStart == start)
            .Select(x => new { x.RowId, x.Version, x.ProjectionVersion }).SingleOrDefaultAsync(token);
        if (state is null) return new(MetricRollupReadStatus.NotFound, null);
        if (!state.Version.AsSpan().SequenceEqual(versionBytes)) return new(MetricRollupReadStatus.Changed, null);
        if (state.ProjectionVersion is { } projection && projection != MetricRollupEncoding.Version)
            return new(MetricRollupReadStatus.UnsupportedVersion, null);
        var query = db.MetricRollupSeries.AsNoTracking().Where(x => x.WindowRowId == state.RowId && x.ServiceId == serviceId);
        if (instanceId is { } instance) query = query.Where(x => x.InstanceId == instance);
        var item = await query.OrderBy(x => x.RowId).Skip((page - 1) * SeriesPageSize + position)
            .Select(x => new { x.ResultVersion, ResultPayload = x.ResultVersion == MetricRollupEncoding.Version ? x.ResultPayload : null }).FirstOrDefaultAsync(token);
        var current = await db.MetricRollupWindows.AsNoTracking().Where(x => x.ServiceId == serviceId && x.WindowStart == start)
            .Select(x => x.Version).SingleOrDefaultAsync(token);
        if (current is null || !current.AsSpan().SequenceEqual(versionBytes)) return new(MetricRollupReadStatus.Changed, null);
        if (item is null) return new(MetricRollupReadStatus.NotFound, null);
        if (item.ResultVersion != MetricRollupEncoding.Version) return new(MetricRollupReadStatus.UnsupportedVersion, null);
        if (item.ResultPayload is null) return new(MetricRollupReadStatus.NotFound, null);
        return new(MetricRollupReadStatus.Found, item.ResultPayload);
    }

    private static byte[] ValidateSeriesQuery(Guid serviceId, DateTimeOffset start, string version, int page, Guid? instanceId)
    {
        if (serviceId.Version != 7 || start.Offset != TimeSpan.Zero || start < DateTimeOffset.UnixEpoch ||
            (start - DateTimeOffset.UnixEpoch).Ticks % TimeSpan.FromMinutes(5).Ticks != 0 ||
            page is < 1 or > MaximumSeriesPage || instanceId is { Version: not 7 } || version is null || version.Length != 12)
            throw new ArgumentException("A service, aligned UTC window, version token and valid page are required.");
        var bytes = new byte[8];
        if (!Convert.TryFromBase64String(version, bytes, out var count) || count != bytes.Length)
            throw new ArgumentException("Invalid window version token.", nameof(version));
        return bytes;
    }

    private static string[] DescribeQuality(long value)
    {
        var reasons = KnownQuality.Where(flag => (value & (long)flag) != 0).Select(flag => flag.ToString()).ToList();
        if ((value & ~KnownQualityMask) != 0) reasons.Add("Unknown");
        return reasons.ToArray();
    }
}
