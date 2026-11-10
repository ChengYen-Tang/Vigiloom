using Google.Protobuf;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Retains first and latest distribution snapshots per cumulative reset epoch without merging quantiles.</summary>
internal static class DistributionSnapshots
{
    internal sealed record Epoch<T>(ulong Start, ulong FirstObserved, ulong LastObserved, T? First, T? Last, int Inputs) where T : class;
    internal sealed record Result<T>(IReadOnlyList<Epoch<T>> Epochs, int Missing, int Invalid, bool UnknownStart,
        bool AmbiguousTimestamps, bool OverlappingEpochs, bool CountDecreased, bool StatisticsUnverified,
        bool UnsupportedFields, bool CapacityExceeded) where T : class;

    internal static Result<T> Reduce<T>(IReadOnlyCollection<T> points, ulong start, ulong end,
        Func<T, ulong> time, Func<T, ulong> epoch, Func<T, uint> flags, Func<T, ulong> count,
        Func<T, (bool Valid, bool StatisticsUnverified)> validate) where T : class, IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(points);
        if (end <= start || points.Count > GaugeWindow.MaximumPoints) throw new ArgumentOutOfRangeException(nameof(points));
        var ordered = points.ToArray();
        if (ordered.Any(point => point is null || time(point) < start || time(point) >= end))
            throw new ArgumentException("Every point must belong to the requested window.", nameof(points));
        Array.Sort(ordered, (left, right) => time(left).CompareTo(time(right)));
        var states = new Dictionary<ulong, State<T>>();
        var closed = new HashSet<ulong>();
        ulong? active = null;
        var missing = 0; var invalid = 0; var unknownStart = false; var ambiguous = false; var overlap = false;
        var decreased = false; var statistics = false; var unsupported = false; var capacity = false;
        var remainingBytes = KnownProtobufFields.MaximumWindowBytes;
        for (var i = 0; i < ordered.Length; i++)
        {
            var point = ordered[i];
            var fields = KnownProtobufFields.Inspect(point, ref remainingBytes);
            if (fields == KnownProtobufFields.Check.Capacity) { capacity = true; break; }
            if (fields == KnownProtobufFields.Check.Unsupported) { unsupported = true; continue; }
            var timestamp = time(point);
            var sourceStart = epoch(point);
            if (i > 0 && time(ordered[i - 1]) == timestamp) ambiguous = true;
            if (!states.TryGetValue(sourceStart, out var state)) states.Add(sourceStart, state = new(timestamp));
            if (active != sourceStart)
            {
                if (active is { } previous)
                {
                    closed.Add(previous);
                    if (sourceStart != 0 && sourceStart < states[previous].LastObserved) overlap = true;
                }
                if (closed.Contains(sourceStart)) overlap = true;
                active = sourceStart;
            }
            state.Inputs++;
            var tied = state.LastObserved == timestamp && state.Inputs > 1;
            state.LastObserved = timestamp;
            if (sourceStart == 0 || sourceStart == timestamp) unknownStart = true;
            var isInvalid = (flags(point) & ~1u) != 0 || sourceStart > timestamp;
            var isMissing = !isInvalid && (flags(point) & 1u) != 0;
            if (!isInvalid && !isMissing)
            {
                var checkedPoint = validate(point);
                isInvalid = !checkedPoint.Valid;
                statistics |= checkedPoint.StatisticsUnverified;
            }
            if (isInvalid) invalid++;
            if (isMissing) missing++;
            if (tied)
            {
                if (state.FirstObserved == timestamp) state.First = null;
                state.Last = null; state.PreviousCount = null;
                continue;
            }
            if (isInvalid || isMissing) { state.Last = null; continue; }
            var currentCount = count(point);
            if (state.PreviousCount is { } previousCount && currentCount < previousCount) decreased = true;
            if (state.Inputs == 1) state.First = point;
            state.Last = point;
            state.PreviousCount = currentCount;
        }
        return new(states.Select(pair => new Epoch<T>(pair.Key, pair.Value.FirstObserved, pair.Value.LastObserved,
                unsupported || capacity ? null : pair.Value.First?.Clone(), unsupported || capacity ? null : pair.Value.Last?.Clone(), pair.Value.Inputs)).ToArray(),
            missing, invalid, unknownStart, ambiguous, overlap, decreased, statistics, unsupported, capacity);
    }

    private sealed class State<T>(ulong firstObserved) where T : class
    {
        internal ulong FirstObserved { get; } = firstObserved;
        internal ulong LastObserved { get; set; } = firstObserved;
        internal T? First { get; set; }
        internal T? Last { get; set; }
        internal ulong? PreviousCount { get; set; }
        internal int Inputs { get; set; }
    }
}
