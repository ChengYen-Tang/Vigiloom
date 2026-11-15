namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record CountEvent(Guid AgentId, Guid MessageId, DateTimeOffset? OccurredAt, DateTimeOffset ReceivedAt,
    IReadOnlyList<string> GroupValues);
internal sealed record CountMatch(Guid AgentId, Guid MessageId, DateTimeOffset FirstEventAt, DateTimeOffset LastEventAt, int Count);
internal sealed record CountResult(IReadOnlyList<CountMatch> Matches, IReadOnlyList<CountEvent> Unassessed, bool CapacityExceeded);

/// <summary>Evaluates bounded event-count windows in event-time order, including out-of-order arrivals.</summary>
internal static class EventCount
{
    internal static CountResult Evaluate(IReadOnlyList<CountEvent> input, TimeSpan window, int minimumCount,
        TimeSpan maximumLateness, TimeSpan maximumFutureSkew, int maximumEvents, CancellationToken token)
    {
        if (window <= TimeSpan.Zero || minimumCount < 1 || maximumLateness < TimeSpan.Zero || maximumFutureSkew < TimeSpan.Zero || maximumEvents < 1)
            throw new ArgumentException("Invalid event-count policy.");
        var seen = new HashSet<(Guid Agent, Guid Message)>();
        var groups = new Dictionary<IReadOnlyList<string>, List<CountEvent>>(GroupComparer.Instance);
        var unassessed = new List<CountEvent>();
        foreach (var item in input)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add((item.AgentId, item.MessageId))) continue;
            if (seen.Count > maximumEvents) return new([], [], true);
            if (item.OccurredAt is not { } occurred || item.GroupValues.Count == 0 || item.GroupValues.Any(x => x is null) ||
                occurred - item.ReceivedAt > maximumFutureSkew || item.ReceivedAt - occurred > maximumLateness)
            {
                unassessed.Add(item);
                continue;
            }
            if (!groups.TryGetValue(item.GroupValues, out var group)) groups.Add(item.GroupValues, group = []);
            group.Add(item);
        }

        var matches = new List<CountMatch>();
        foreach (var group in groups.Values)
        {
            group.Sort(static (left, right) =>
            {
                var order = left.OccurredAt!.Value.CompareTo(right.OccurredAt!.Value);
                if (order != 0) return order;
                order = left.ReceivedAt.CompareTo(right.ReceivedAt);
                if (order != 0) return order;
                order = left.AgentId.CompareTo(right.AgentId);
                return order != 0 ? order : left.MessageId.CompareTo(right.MessageId);
            });
            var start = 0;
            for (var end = 0; end < group.Count; end++)
            {
                token.ThrowIfCancellationRequested();
                var time = group[end].OccurredAt!.Value;
                while (start < end && time - group[start].OccurredAt!.Value > window) start++;
                var count = end - start + 1;
                if (count >= minimumCount)
                    matches.Add(new(group[end].AgentId, group[end].MessageId, group[start].OccurredAt!.Value, time, count));
            }
        }
        return new(matches, unassessed, false);
    }

    internal sealed class GroupComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        internal static readonly GroupComparer Instance = new();
        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            for (var i = 0; i < x.Count; i++)
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }
        public int GetHashCode(IReadOnlyList<string> values)
        {
            var hash = new HashCode();
            foreach (var value in values) hash.Add(value, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
