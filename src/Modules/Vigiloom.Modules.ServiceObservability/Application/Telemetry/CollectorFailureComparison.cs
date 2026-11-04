namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

public enum FailureObservation { Unknown, Stale, Increased, Unchanged }
internal readonly record struct FailureSample(ulong MeasuredAt, ulong StartedAt, DateTimeOffset ReceivedAt,
    bool Supported, bool Missing, long? Integer, double? Floating);
internal readonly record struct FailureComparison(FailureObservation Observation, ulong? From, ulong? Through);

/// <summary>Compares two observations of one fully identified cumulative series, never a delivery or health verdict.</summary>
internal static class CollectorFailureComparison
{
    internal static FailureComparison Evaluate(IReadOnlyList<FailureSample> samples, DateTimeOffset through,
        bool complete, bool active, TimeSpan freshness, TimeSpan maximumGap)
    {
        if (!complete || !active || freshness <= TimeSpan.Zero || maximumGap <= TimeSpan.Zero || samples.Count == 0)
            return new(FailureObservation.Unknown, null, null);

        var groups = samples.OrderByDescending(x => x.MeasuredAt).GroupBy(x => x.MeasuredAt).Take(2).ToArray();
        var latestGroup = groups[0].ToArray();
        var latest = latestGroup[0];
        if (latest.MeasuredAt == 0 || latestGroup.Any(x => !Equivalent(latest, x)) || !Valid(latest))
            return new(FailureObservation.Unknown, null, null);
        var latestAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(latest.MeasuredAt / 1_000_000));
        if (latestAt > through || latestGroup.Any(x => x.ReceivedAt > through)) return new(FailureObservation.Unknown, null, null);
        if (through - latestAt > freshness || latestGroup.Min(x => x.ReceivedAt) < through - freshness)
            return new(FailureObservation.Stale, null, latest.MeasuredAt);
        if (groups.Length < 2) return new(FailureObservation.Unknown, null, latest.MeasuredAt);

        var earlierGroup = groups[1].ToArray();
        var earlier = earlierGroup[0];
        if (earlierGroup.Any(x => !Equivalent(earlier, x)) || !Valid(earlier) || earlier.StartedAt != latest.StartedAt ||
            earlier.MeasuredAt >= latest.MeasuredAt || (latest.MeasuredAt - earlier.MeasuredAt) / 100 > (ulong)maximumGap.Ticks ||
            earlierGroup.Any(x => x.ReceivedAt > through) || earlier.Integer.HasValue != latest.Integer.HasValue)
            return new(FailureObservation.Unknown, earlier.MeasuredAt, latest.MeasuredAt);
        int comparison = latest.Integer is { } currentInteger
            ? currentInteger.CompareTo(earlier.Integer!.Value)
            : latest.Floating!.Value.CompareTo(earlier.Floating!.Value);
        return new(comparison < 0 ? FailureObservation.Unknown : comparison > 0 ? FailureObservation.Increased : FailureObservation.Unchanged,
            earlier.MeasuredAt, latest.MeasuredAt);
    }

    private static bool Valid(FailureSample sample) => sample.Supported && !sample.Missing && sample.StartedAt > 0 &&
        sample.StartedAt <= sample.MeasuredAt && sample.Integer.HasValue != sample.Floating.HasValue &&
        (sample.Integer is >= 0 || sample.Floating is { } value && double.IsFinite(value) && value >= 0);

    // ReceivedAt is deliberately excluded: identical measurements can be retransmitted.
    private static bool Equivalent(FailureSample left, FailureSample right) => left.MeasuredAt == right.MeasuredAt &&
        left.StartedAt == right.StartedAt && left.Supported == right.Supported && left.Missing == right.Missing &&
        left.Integer == right.Integer && Nullable.Equals(left.Floating, right.Floating);
}
