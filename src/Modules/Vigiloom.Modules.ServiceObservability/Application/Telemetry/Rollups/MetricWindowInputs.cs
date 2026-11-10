using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Reads bounded candidates for one service and one five-minute window.</summary>
internal static class MetricWindowInputs
{
    internal const int MaximumPoints = 10_000;
    internal const long MaximumBytes = 16 * 1024 * 1024;
    internal sealed record Result(IReadOnlyList<MetricPoint> Points, bool CapacityExceeded);

    /// <remarks>The caller must authorize the service and coordinate concurrent writes and cleanup.
    /// The inclusive SQL upper bound deliberately includes boundary candidates. The caller must
    /// classify each original nanosecond endpoint using its metric type and temporality.</remarks>
    internal static async Task<Result> ReadAsync(ServiceObservabilityDbContext db, Guid serviceId, MetricWindow window,
        CancellationToken token, int maxPoints = MaximumPoints, long maxBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (serviceId.Version != 7 || window.Start % MetricWindow.WidthNanoseconds != 0 ||
            window.End <= window.Start || window.End - window.Start != MetricWindow.WidthNanoseconds ||
            maxPoints is < 1 or > MaximumPoints || maxBytes is < 1 or > MaximumBytes)
            throw new ArgumentException("A service, aligned five-minute window and bounded limits are required.");
        var from = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(window.Start / 100)));
        var through = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(window.End / 100)));
        var points = new List<MetricPoint>(Math.Min(maxPoints, 256));
        long bytes = 0;
        var query = db.MetricPoints.AsNoTrackingWithIdentityResolution().Include(point => point.Context)
            .Where(point => point.ServiceId == serviceId && point.OccurredAt >= from && point.OccurredAt <= through)
            .OrderBy(point => point.OccurredAt).ThenBy(point => point.RowId).Take(maxPoints + 1);
        await foreach (var point in query.AsAsyncEnumerable().WithCancellation(token))
        {
            // Charge repeated context to each point; this is conservative when EF shares an instance.
            var size = (long)point.PointPayload.Length + point.Context.ResourcePayload.Length +
                point.Context.ScopePayload.Length + point.Context.MetricPayload.Length +
                2L * (point.Context.ResourceSchemaUrl.Length + point.Context.ScopeSchemaUrl.Length);
            if (points.Count == maxPoints || size > maxBytes - bytes) return new([], true);
            points.Add(point);
            bytes += size;
        }
        return new(points, false);
    }
}
