using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Merges compatible delta histograms only for a fully covered, non-overlapping window.</summary>
internal static class DeltaHistogramWindow
{
    private const int MaximumBucketWork = 1_000_000;
    // A verified bound is only a necessary consistency check, not proof that every source sample is truthful.
    internal sealed record Result(HistogramDataPoint? Aggregate, int Missing, int Invalid, bool Gap,
        bool Overlap, bool CrossesBoundary, bool IncompatibleBounds, bool Overflow, bool CapacityExceeded,
        bool ExemplarsOmitted, bool SumBoundsUnverified, bool UnsupportedFields);

    internal static Result Reduce(IReadOnlyCollection<HistogramDataPoint> points, ulong start, ulong end)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (end <= start || points.Count > GaugeWindow.MaximumPoints) throw new ArgumentOutOfRangeException(nameof(points));
        var ordered = points.ToArray();
        if (ordered.Any(point => point is null || point.TimeUnixNano <= start || point.TimeUnixNano > end))
            throw new ArgumentException("Every delta endpoint must lie in the window (start, end].", nameof(points));
        Array.Sort(ordered, static (left, right) => left.TimeUnixNano.CompareTo(right.TimeUnixNano));
        HistogramDataPoint? result = null;
        var coverage = new DeltaCoverage(start, end);
        var missing = 0;
        var invalid = 0;
        var incompatible = false;
        var overflow = false;
        var capacity = false;
        var unsupported = false;
        var remainingBytes = KnownProtobufFields.MaximumWindowBytes;
        var exemplars = false;
        var sumBoundsUnverified = false;
        var bucketWork = 0;
        double sum = 0;
        double correction = 0;
        foreach (var point in ordered)
        {
            var fields = KnownProtobufFields.Inspect(point, ref remainingBytes);
            if (fields == KnownProtobufFields.Check.Capacity) { capacity = true; break; }
            if (fields == KnownProtobufFields.Check.Unsupported) { unsupported = true; continue; }
            if (point.BucketCounts.Count > MaximumBucketWork - bucketWork) { capacity = true; break; }
            bucketWork += point.BucketCounts.Count;
            if ((point.Flags & ~1u) != 0) { invalid++; continue; }
            if ((point.Flags & 1u) != 0 || point.StartTimeUnixNano == 0) { missing++; continue; }
            if (point.StartTimeUnixNano >= point.TimeUnixNano) { invalid++; continue; }
            if (!Valid(point, out var unverified)) { invalid++; continue; }
            sumBoundsUnverified |= unverified;
            if (!coverage.Observe(point.StartTimeUnixNano, point.TimeUnixNano)) continue;
            exemplars |= point.Exemplars.Count > 0;
            if (result is null)
            {
                result = point.Clone();
                result.Exemplars.Clear();
                if (point.HasSum) sum = point.Sum;
                continue;
            }
            if (!result.ExplicitBounds.SequenceEqual(point.ExplicitBounds) || result.BucketCounts.Count != point.BucketCounts.Count)
            { incompatible = true; continue; }
            try
            {
                result.Count = checked(result.Count + point.Count);
                for (var i = 0; i < result.BucketCounts.Count; i++)
                    result.BucketCounts[i] = checked(result.BucketCounts[i] + point.BucketCounts[i]);
            }
            catch (OverflowException) { overflow = true; }
            if (result.HasSum && point.HasSum)
            {
                CompensatedSum.Add(ref sum, ref correction, point.Sum);
                result.Sum = sum + correction;
            }
            else result.ClearSum();
            if (result.HasMin && point.HasMin) result.Min = Math.Min(result.Min, point.Min);
            else result.ClearMin();
            if (result.HasMax && point.HasMax) result.Max = Math.Max(result.Max, point.Max);
            else result.ClearMax();
            if (result.HasSum && !double.IsFinite(result.Sum)) overflow = true;
            result.TimeUnixNano = point.TimeUnixNano;
        }
        coverage.Complete();
        var complete = ordered.Length > 0 && missing == 0 && invalid == 0 && !coverage.Gap && !coverage.Overlap && !coverage.CrossesBoundary && !incompatible && !overflow && !capacity && !unsupported;
        if (complete) { result!.StartTimeUnixNano = start; result.TimeUnixNano = end; }
        return new(complete ? result : null, missing, invalid, coverage.Gap, coverage.Overlap, coverage.CrossesBoundary, incompatible, overflow, capacity, exemplars, sumBoundsUnverified, unsupported);
    }

    private static bool Valid(HistogramDataPoint point, out bool sumBoundsUnverified)
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
