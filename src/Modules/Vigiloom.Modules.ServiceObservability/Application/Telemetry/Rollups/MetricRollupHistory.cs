using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Lists internal window states without exposing row identifiers or telemetry payloads.</summary>
internal sealed class MetricRollupHistory(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal const int PageSize = 100;

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
            .Select(x => new { x.WindowStart, x.InputRevision, x.ProjectedRevision, x.ProjectionVersion, x.ProjectedAt, x.IsComplete, x.RawPruned })
            .Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Take(PageSize).Select(x => new MetricRollupWindowResponse(x.WindowStart, x.WindowStart.AddMinutes(5),
            x.ProjectedRevision is null ? "Pending" :
            x.ProjectionVersion != MetricRollupEncoding.Version ? "UnsupportedVersion" :
            x.ProjectedRevision != x.InputRevision ? "Stale" : x.IsComplete ? "Current" : "Incomplete",
            x.ProjectedAt, x.ProjectedRevision is not null, x.ProjectedRevision is not null && x.ProjectedRevision != x.InputRevision,
            x.IsComplete, x.ProjectionVersion is null || x.ProjectionVersion == MetricRollupEncoding.Version, x.RawPruned)).ToArray();
        return new(items, rows.Length > PageSize ? items[^1].WindowStart : null);
    }
}
