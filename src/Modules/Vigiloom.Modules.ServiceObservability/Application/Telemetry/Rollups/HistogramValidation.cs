using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Checks one explicit-boundary histogram without changing its temporality.</summary>
internal static class HistogramValidation
{
    internal static bool Valid(HistogramDataPoint point, out bool sumBoundsUnverified)
    {
        if (point.BucketCounts.Count == 0 && point.ExplicitBounds.Count != 0 ||
            point.BucketCounts.Count != 0 && point.BucketCounts.Count != point.ExplicitBounds.Count + 1)
        {
            sumBoundsUnverified = false;
            return false;
        }
        if (!HistogramStatistics.Valid(point.Count, point.HasSum, point.Sum, point.HasMin, point.Min, point.HasMax, point.Max,
                out sumBoundsUnverified)) return false;
        for (var i = 0; i < point.ExplicitBounds.Count; i++)
            if (!double.IsFinite(point.ExplicitBounds[i]) || i > 0 && point.ExplicitBounds[i - 1] >= point.ExplicitBounds[i]) return false;
        ulong total = 0;
        try { foreach (var count in point.BucketCounts) total = checked(total + count); }
        catch (OverflowException) { return false; }
        if (point.BucketCounts.Count == 0)
        {
            if (point.Count == 1 && point.HasSum && !point.HasMin && !point.HasMax) sumBoundsUnverified = true;
            return true;
        }
        if (total != point.Count) return false;
        if (point.Count == 0) return true;
        var first = 0;
        while (point.BucketCounts[first] == 0) first++;
        var last = point.BucketCounts.Count - 1;
        while (point.BucketCounts[last] == 0) last--;
        if (point.Count == 1 && point.HasSum && !InBucket(point.ExplicitBounds, first, point.Sum)) return false;
        return (!point.HasMin || InBucket(point.ExplicitBounds, first, point.Min)) &&
            (!point.HasMax || InBucket(point.ExplicitBounds, last, point.Max));
    }

    private static bool InBucket(IList<double> bounds, int bucket, double value) =>
        (bucket == 0 || value > bounds[bucket - 1]) && (bucket == bounds.Count || value <= bounds[bucket]);
}
