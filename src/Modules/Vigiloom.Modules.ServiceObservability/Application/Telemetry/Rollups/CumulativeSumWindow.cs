using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Preserves cumulative counter snapshots by reset epoch without adding them together.</summary>
internal static class CumulativeSumWindow
{
    internal sealed record Epoch(ulong Start, ulong FirstObserved, ulong LastObserved,
        NumberDataPoint? First, NumberDataPoint? Last, int Inputs);
    internal sealed record Result(IReadOnlyList<Epoch> Epochs, int Missing, int Invalid,
        bool UnknownStart, bool AmbiguousTimestamps, bool OverlappingEpochs, bool Decreased, bool MonotonicityUnknown);

    internal static Result Reduce(IReadOnlyCollection<NumberDataPoint> points, ulong start, ulong end, bool monotonic)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (end <= start || points.Count > GaugeWindow.MaximumPoints) throw new ArgumentOutOfRangeException(nameof(points));
        var ordered = points.ToArray();
        if (ordered.Any(point => point is null || point.TimeUnixNano < start || point.TimeUnixNano >= end))
            throw new ArgumentException("Every point must belong to the requested window.", nameof(points));
        Array.Sort(ordered, static (left, right) => left.TimeUnixNano.CompareTo(right.TimeUnixNano));
        var epochs = new Dictionary<ulong, State>();
        var closed = new HashSet<ulong>();
        ulong? active = null;
        var missing = 0;
        var invalid = 0;
        var unknownStart = false;
        var ambiguous = false;
        var overlap = false;
        var decreased = false;
        var unknownOrder = false;
        for (var i = 0; i < ordered.Length; i++)
        {
            var point = ordered[i];
            var timestamp = point.TimeUnixNano;
            if (i > 0 && ordered[i - 1].TimeUnixNano == timestamp) ambiguous = true;
            if (!epochs.TryGetValue(point.StartTimeUnixNano, out var state))
            {
                state = new State(timestamp);
                epochs.Add(point.StartTimeUnixNano, state);
            }
            if (active != point.StartTimeUnixNano)
            {
                if (active is { } previous)
                {
                    closed.Add(previous);
                    if (point.StartTimeUnixNano != 0 && point.StartTimeUnixNano < epochs[previous].LastObserved) overlap = true;
                }
                if (closed.Contains(point.StartTimeUnixNano)) overlap = true;
                active = point.StartTimeUnixNano;
            }
            state.Inputs++;
            var tied = state.LastObserved == timestamp && state.Inputs > 1;
            state.LastObserved = timestamp;
            if (point.StartTimeUnixNano == 0 || point.StartTimeUnixNano == timestamp) unknownStart = true;
            var isInvalid = (point.Flags & ~1u) != 0 || point.StartTimeUnixNano > timestamp;
            var isMissing = !isInvalid && ((point.Flags & 1u) != 0 || point.ValueCase == NumberDataPoint.ValueOneofCase.None);
            if (!isInvalid && !isMissing && point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble && !double.IsFinite(point.AsDouble))
                isInvalid = true;
            if (!isInvalid && !isMissing && monotonic &&
                (point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt && point.AsInt < 0 ||
                 point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble && point.AsDouble < 0)) isInvalid = true;
            if (isInvalid) invalid++;
            if (isMissing) missing++;
            if (tied)
            {
                if (state.FirstObserved == timestamp) state.First = null;
                state.Last = null;
                state.Previous = null;
                continue;
            }
            if (isInvalid || isMissing)
            {
                state.Last = null;
                continue;
            }
            if (monotonic && state.Previous is { } previousPoint)
            {
                if (previousPoint.ValueCase != point.ValueCase) unknownOrder = true;
                else if (point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt && point.AsInt < previousPoint.AsInt ||
                         point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble && point.AsDouble < previousPoint.AsDouble)
                    decreased = true;
            }
            if (state.Inputs == 1) state.First = point;
            state.Last = point;
            state.Previous = point;
        }
        return new(epochs.Select(pair => new Epoch(pair.Key, pair.Value.FirstObserved, pair.Value.LastObserved,
                pair.Value.First?.Clone(), pair.Value.Last?.Clone(), pair.Value.Inputs)).ToArray(),
            missing, invalid, unknownStart, ambiguous, overlap, decreased, unknownOrder);
    }

    private sealed class State(ulong firstObserved)
    {
        internal ulong FirstObserved { get; } = firstObserved;
        internal ulong LastObserved { get; set; } = firstObserved;
        internal NumberDataPoint? First { get; set; }
        internal NumberDataPoint? Last { get; set; }
        internal NumberDataPoint? Previous { get; set; }
        internal int Inputs { get; set; }
    }
}
