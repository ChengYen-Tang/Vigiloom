namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal readonly record struct HealthTimelineInput(Guid TargetId, EvaluatedHealth State, DateTimeOffset Start, DateTimeOffset End);
internal readonly record struct HealthTimelineSegment(EvaluatedHealth State, DateTimeOffset Start, DateTimeOffset End, int UnknownParticipants);
internal sealed record HealthTimelineResult(IReadOnlyList<HealthTimelineSegment> Segments, TimeSpan Healthy,
    TimeSpan Degraded, TimeSpan Unhealthy, TimeSpan Unknown)
{
    internal double? Availability => Healthy + Degraded + Unhealthy == TimeSpan.Zero ? null :
        Healthy.Ticks / (double)(Healthy + Degraded + Unhealthy).Ticks;
    internal double Coverage => (Healthy + Degraded + Unhealthy).Ticks / (double)(Healthy + Degraded + Unhealthy + Unknown).Ticks;
}

/// <summary>Derives service intervals from non-overlapping target intervals in a bounded UTC window.</summary>
internal static class HealthTimeline
{
    private readonly record struct Boundary(DateTimeOffset At, Guid TargetId, EvaluatedHealth State, bool Starts);

    internal static HealthTimelineResult Aggregate(DateTimeOffset from, DateTimeOffset to, IEnumerable<HealthTimelineInput> inputs)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Invalid health history window.");
        var events = new List<Boundary>();
        foreach (var input in inputs)
        {
            if (input.TargetId.Version != 7 || input.Start.Offset != TimeSpan.Zero || input.End.Offset != TimeSpan.Zero ||
                input.Start >= input.End || input.State is < EvaluatedHealth.Unknown or > EvaluatedHealth.Unhealthy)
                throw new ArgumentException("Invalid health history interval.");
            var start = input.Start > from ? input.Start : from;
            var end = input.End < to ? input.End : to;
            if (start >= end) continue;
            events.Add(new(start, input.TargetId, input.State, true));
            events.Add(new(end, input.TargetId, input.State, false));
        }
        events.Sort(static (a, b) =>
        {
            var order = a.At.CompareTo(b.At);
            return order != 0 ? order : a.Starts.CompareTo(b.Starts);
        });
        var active = new Dictionary<Guid, EvaluatedHealth>();
        var segments = new List<HealthTimelineSegment>();
        var cursor = from;
        var position = 0;
        while (position < events.Count)
        {
            var at = events[position].At;
            Append(segments, active.Values, cursor, at);
            while (position < events.Count && events[position].At == at)
            {
                var current = events[position++];
                if (current.Starts)
                {
                    if (!active.TryAdd(current.TargetId, current.State)) throw new InvalidOperationException("Overlapping target health intervals.");
                }
                else if (!active.Remove(current.TargetId)) throw new InvalidOperationException("Unmatched target health interval.");
            }
            cursor = at;
        }
        Append(segments, active.Values, cursor, to);
        long healthy = 0, degraded = 0, unhealthy = 0, unknown = 0;
        foreach (var segment in segments)
        {
            var ticks = (segment.End - segment.Start).Ticks;
            switch (segment.State)
            {
                case EvaluatedHealth.Healthy: healthy += ticks; break;
                case EvaluatedHealth.Degraded: degraded += ticks; break;
                case EvaluatedHealth.Unhealthy: unhealthy += ticks; break;
                default: unknown += ticks; break;
            }
        }
        return new(segments, TimeSpan.FromTicks(healthy), TimeSpan.FromTicks(degraded), TimeSpan.FromTicks(unhealthy), TimeSpan.FromTicks(unknown));
    }

    private static void Append(List<HealthTimelineSegment> segments, IEnumerable<EvaluatedHealth> active, DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to) return;
        var aggregate = HealthAggregation.Combine(active);
        if (segments.Count > 0 && segments[^1] is var last && last.End == from &&
            last.State == aggregate.State && last.UnknownParticipants == aggregate.Unknown)
            segments[^1] = last with { End = to };
        else segments.Add(new(aggregate.State, from, to, aggregate.Unknown));
    }
}
