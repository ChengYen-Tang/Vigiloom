using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>A sample-based reduction for one already identified OTLP gauge stream.</summary>
internal static class GaugeWindow
{
    internal const int MaximumPoints = 100_000;

    internal sealed record Result(NumberDataPoint? Latest, int Inputs, int Missing, int Invalid,
        bool AmbiguousTimestamps, bool UnsupportedFields, bool CapacityExceeded, ulong? FirstObserved, ulong? LastObserved);

    /// <remarks>
    /// Callers must supply points for one authenticated stream. A missing or ambiguous latest
    /// observation remains unknown; the last earlier value is not substituted for it.
    /// </remarks>
    internal static Result Reduce(IReadOnlyCollection<NumberDataPoint> points, ulong start, ulong end)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (end <= start || points.Count > MaximumPoints) throw new ArgumentOutOfRangeException(nameof(points));
        var ordered = points.ToArray();
        if (ordered.Any(point => point is null || point.TimeUnixNano < start || point.TimeUnixNano >= end))
            throw new ArgumentException("Every point must belong to the requested window.", nameof(points));
        Array.Sort(ordered, static (left, right) => left.TimeUnixNano.CompareTo(right.TimeUnixNano));
        var missing = 0;
        var invalid = 0;
        var ambiguous = false;
        var unsupported = false;
        var capacity = false;
        var remainingBytes = KnownProtobufFields.MaximumWindowBytes;
        for (var i = 0; i < ordered.Length; i++)
        {
            var point = ordered[i];
            var fields = KnownProtobufFields.Inspect(point, ref remainingBytes);
            if (fields == KnownProtobufFields.Check.Capacity) { capacity = true; break; }
            if (fields == KnownProtobufFields.Check.Unsupported) { unsupported = true; continue; }
            if (i > 0 && ordered[i - 1].TimeUnixNano == point.TimeUnixNano) ambiguous = true;
            if ((point.Flags & ~1u) != 0)
                invalid++;
            else if ((point.Flags & 1u) != 0 || point.ValueCase == NumberDataPoint.ValueOneofCase.None)
                missing++;
            else if (point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble && !double.IsFinite(point.AsDouble))
                invalid++;
        }
        NumberDataPoint? latest = null;
        if (ordered.Length > 0 && !unsupported && !capacity)
        {
            var last = ordered[^1];
            var tied = ordered.Length > 1 && ordered[^2].TimeUnixNano == last.TimeUnixNano;
            if (!tied && last.Flags == 0 && last.ValueCase != NumberDataPoint.ValueOneofCase.None &&
                (last.ValueCase != NumberDataPoint.ValueOneofCase.AsDouble || double.IsFinite(last.AsDouble)))
                latest = last.Clone();
        }
        return new(latest, ordered.Length, missing, invalid, ambiguous, unsupported, capacity,
            ordered.Length == 0 ? null : ordered[0].TimeUnixNano, ordered.Length == 0 ? null : ordered[^1].TimeUnixNano);
    }
}
