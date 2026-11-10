using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Reduces one delta-sum series only when its intervals exactly cover the window.</summary>
internal static class DeltaSumWindow
{
    internal sealed record Result(long? Integer, double? Floating, int Inputs, int Missing, int Invalid,
        bool Gap, bool Overlap, bool CrossesBoundary, bool Overflow, bool UnsupportedFields, bool CapacityExceeded);

    internal static Result Reduce(IReadOnlyCollection<NumberDataPoint> points, ulong start, ulong end, bool monotonic)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (end <= start || points.Count > GaugeWindow.MaximumPoints) throw new ArgumentOutOfRangeException(nameof(points));
        var ordered = points.ToArray();
        if (ordered.Any(point => point is null || point.TimeUnixNano <= start || point.TimeUnixNano > end))
            throw new ArgumentException("Every delta endpoint must lie in the window (start, end].", nameof(points));
        Array.Sort(ordered, static (left, right) => left.TimeUnixNano.CompareTo(right.TimeUnixNano));
        var missing = 0;
        var invalid = 0;
        var coverage = new DeltaCoverage(start, end);
        var overflow = false;
        var unsupported = false;
        var capacity = false;
        var remainingBytes = KnownProtobufFields.MaximumWindowBytes;
        long integer = 0;
        double floating = 0;
        double correction = 0;
        var hasDouble = false;
        foreach (var point in ordered)
        {
            var fields = KnownProtobufFields.Inspect(point, ref remainingBytes);
            if (fields == KnownProtobufFields.Check.Capacity) { capacity = true; break; }
            if (fields == KnownProtobufFields.Check.Unsupported) { unsupported = true; continue; }
            if ((point.Flags & ~1u) != 0) { invalid++; continue; }
            if ((point.Flags & 1u) != 0 || point.ValueCase == NumberDataPoint.ValueOneofCase.None || point.StartTimeUnixNano == 0)
            { missing++; continue; }
            if (point.StartTimeUnixNano >= point.TimeUnixNano) { invalid++; continue; }
            if (!coverage.Observe(point.StartTimeUnixNano, point.TimeUnixNano)) continue;
            if (point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt)
            {
                var value = point.AsInt;
                if (monotonic && value < 0) { invalid++; continue; }
                try { integer = checked(integer + value); }
                catch (OverflowException) { overflow = true; }
            }
            else
            {
                var value = point.AsDouble;
                if (!double.IsFinite(value) || monotonic && value < 0) { invalid++; continue; }
                hasDouble = true;
                CompensatedSum.Add(ref floating, ref correction, value);
            }
        }
        coverage.Complete();
        if (hasDouble && integer is > 9_007_199_254_740_992L or < -9_007_199_254_740_992L) overflow = true;
        if (hasDouble && !overflow) CompensatedSum.Add(ref floating, ref correction, integer);
        floating += correction;
        if (!double.IsFinite(floating)) overflow = true;
        var complete = ordered.Length > 0 && missing == 0 && invalid == 0 && !coverage.Gap && !coverage.Overlap && !coverage.CrossesBoundary && !overflow && !unsupported && !capacity;
        return new(complete && !hasDouble ? integer : null, complete && hasDouble ? floating : null,
            ordered.Length, missing, invalid, coverage.Gap, coverage.Overlap, coverage.CrossesBoundary, overflow, unsupported, capacity);
    }

}
