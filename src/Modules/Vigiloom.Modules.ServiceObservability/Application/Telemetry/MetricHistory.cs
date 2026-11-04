using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Bounded service metric history. The caller must enforce read authorization.</summary>
internal sealed class MetricHistory(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal const int PageSize = 25;
    internal const int MaximumPage = 100;
    internal sealed record Page(IReadOnlyList<MetricPoint> Items, bool HasMore, bool Truncated);

    internal async Task<Page> QueryAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero ||
            to <= from || to - from > TimeSpan.FromDays(31) || page is < 1 or > MaximumPage)
            throw new ArgumentException("A service, UTC range up to 31 days, and page from 1 to 100 are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.MetricPoints.AsNoTrackingWithIdentityResolution().Include(x => x.Context)
            .Where(x => x.ServiceId == serviceId && x.OccurredAt >= from && x.OccurredAt < to)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        return new(rows.Take(PageSize).ToArray(), rows.Length > PageSize && page < MaximumPage,
            rows.Length > PageSize && page == MaximumPage);
    }
}
