using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record CorrelationObservation(HostLogEntry Event, SigmaMatch Result, CountMatch? Match, bool IsCandidate = false);
internal sealed record CorrelationEvaluationResult(IReadOnlyList<CorrelationObservation> Observations, bool CapacityExceeded);

/// <summary>Evaluates a complete bounded event set against a resolved correlation plan.</summary>
internal static class CorrelationEvaluation
{
    internal static CorrelationEvaluationResult Evaluate(CorrelationPlan plan, IReadOnlyList<HostLogEntry> events,
        TimeSpan maximumLateness, TimeSpan maximumFutureSkew, int maximumEvents, CancellationToken token,
        IReadOnlyList<SigmaRuleException>? exceptions = null)
    {
        if (maximumEvents < 1) throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        var unique = new Dictionary<(Guid Agent, Guid Message), HostLogEntry>();
        foreach (var item in events)
        {
            token.ThrowIfCancellationRequested();
            unique.TryAdd((item.AgentId, item.MessageId), item);
            if (unique.Count > maximumEvents) return new([], true);
        }

        var preliminary = new Dictionary<(Guid Agent, Guid Message), SigmaMatch>(unique.Count);
        var candidates = new List<CountEvent>(unique.Count);
        var groups = new Dictionary<(Guid Agent, Guid Message), IReadOnlyList<string>>();
        var uncertain = new Dictionary<IReadOnlyList<string>, List<DateTimeOffset>>(EventCount.GroupComparer.Instance);
        var groupUnknownTime = new HashSet<IReadOnlyList<string>>(EventCount.GroupComparer.Instance);
        var globalUncertain = new List<DateTimeOffset>();
        var globalUnknownTime = false;
        foreach (var (id, log) in unique)
        {
            token.ThrowIfCancellationRequested();
            var decoded = HostEventDecoder.Decode(log);
            var match = SigmaExceptions.Apply(plan.BaseRule.Rule.Match(decoded), decoded, exceptions);
            IReadOnlyList<string>? group = null;
            if (match != SigmaMatch.No)
            {
                var values = new string[plan.Rule.GroupBy.Count];
                var complete = true;
                for (var i = 0; i < values.Length; i++)
                {
                    if (!decoded.Fields.TryGetValue(plan.Rule.GroupBy[i], out var field) || field.Count != 1)
                    {
                        complete = false;
                        break;
                    }
                    values[i] = field[0];
                }
                if (complete) group = values;
                else match = SigmaMatch.Unknown;
                if (match == SigmaMatch.Yes && group is not null)
                {
                    groups.Add(id, group);
                    candidates.Add(new(log.AgentId, log.MessageId, log.OccurredAt, log.ReceivedAt, values));
                }
            }
            preliminary.Add(id, match);
            if (match == SigmaMatch.Unknown) AddUncertainty(log.OccurredAt, group);
        }

        var counts = EventCount.Evaluate(candidates, plan.Rule.Window, plan.Rule.MinimumCount,
            maximumLateness, maximumFutureSkew, maximumEvents, token);
        foreach (var item in counts.Unassessed)
        {
            preliminary[(item.AgentId, item.MessageId)] = SigmaMatch.Unknown;
            AddUncertainty(item.OccurredAt, item.GroupValues);
        }
        globalUncertain.Sort();
        foreach (var times in uncertain.Values) times.Sort();
        var matches = counts.Matches.ToDictionary(x => (x.AgentId, x.MessageId));
        var observations = unique.Select(pair =>
        {
            var (id, log) = pair;
            var initial = preliminary[id];
            if (initial != SigmaMatch.Yes) return new CorrelationObservation(log, initial, null);
            if (matches.TryGetValue(id, out var match)) return new CorrelationObservation(log, SigmaMatch.Yes, match, true);
            var time = log.OccurredAt!.Value.ToUniversalTime();
            var start = time.UtcTicks < plan.Rule.Window.Ticks ? DateTimeOffset.MinValue : time - plan.Rule.Window;
            var group = groups[id];
            var unknown = globalUnknownTime || groupUnknownTime.Contains(group) || InWindow(globalUncertain, start, time) ||
                uncertain.TryGetValue(group, out var times) && InWindow(times, start, time);
            return new CorrelationObservation(log, unknown ? SigmaMatch.Unknown : SigmaMatch.No, null, true);
        }).ToArray();
        return new(observations, false);

        void AddUncertainty(DateTimeOffset? occurredAt, IReadOnlyList<string>? group)
        {
            if (group is null)
            {
                if (occurredAt is { } time) globalUncertain.Add(time);
                else globalUnknownTime = true;
            }
            else if (occurredAt is { } time)
            {
                if (!uncertain.TryGetValue(group, out var times)) uncertain.Add(group, times = []);
                times.Add(time);
            }
            else groupUnknownTime.Add(group);
        }
    }

    internal static CorrelationEvaluationResult ApplyCoverage(CorrelationEvaluationResult result, TimeSpan window,
        DateTimeOffset completeSince, DateTimeOffset? incompleteUntil)
    {
        if (result.CapacityExceeded) return result;
        return result with { Observations = result.Observations.Select(item =>
        {
            if (!item.IsCandidate || item.Result != SigmaMatch.No || item.Event.OccurredAt is not { } occurred) return item;
            var time = occurred.ToUniversalTime();
            var start = time.UtcTicks < window.Ticks ? DateTimeOffset.MinValue : time - window;
            return start < completeSince || incompleteUntil is { } until && time <= until
                ? item with { Result = SigmaMatch.Unknown } : item;
        }).ToArray() };
    }

    private static bool InWindow(List<DateTimeOffset> times, DateTimeOffset start, DateTimeOffset end)
    {
        var index = LowerBound(times, start);
        return index < times.Count && times[index] <= end;
    }

    private static int LowerBound(List<DateTimeOffset> values, DateTimeOffset target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
