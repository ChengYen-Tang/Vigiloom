using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record SigmaReplaySample(HostLogEntry Event, SigmaMatch Expected);
internal sealed record SigmaReplayObservation(Guid AgentId, Guid MessageId, SigmaMatch Expected, SigmaMatch Actual);
internal sealed record SigmaReplayResult(SigmaSnapshot Snapshot, SigmaSourceRule Source,
    IReadOnlyList<SigmaReplayObservation> Observations)
{
    // An inconclusive event is never evidence of a successful negative test.
    internal bool Passed => Observations.Any(x => x.Expected == SigmaMatch.Yes) && Observations.Any(x => x.Expected == SigmaMatch.No) &&
        Observations.All(x => x.Actual == x.Expected);
}

/// <summary>Runs real retained event content through the same decoder and rule as live detection.</summary>
internal static class SigmaReplay
{
    internal static SigmaReplayResult RunCorrelation(SigmaRuleSet ruleSet, string path,
        IReadOnlyList<SigmaReplaySample> samples, TimeSpan maximumLateness, TimeSpan maximumFutureSkew, int maximumEvents,
        CancellationToken token, IReadOnlyList<SigmaRuleException>? exceptions = null)
    {
        var snapshot = ruleSet.Snapshot;
        if (snapshot.Availability != SigmaAvailability.Ready || snapshot.RevisionId is null)
            throw new InvalidOperationException("The rule source is not ready for testing.");
        if (maximumEvents < 1 || samples.Count > maximumEvents) throw new ArgumentException("The replay exceeds the event limit.", nameof(samples));
        var rule = ruleSet.Correlations.SingleOrDefault(x => string.Equals(x.Source.Path, path, StringComparison.Ordinal))
            ?? throw new ArgumentException("The rule is not available in this source revision.", nameof(path));
        var evaluation = CorrelationEvaluation.Evaluate(rule.Plan, samples.Select(x => x.Event).ToArray(),
            maximumLateness, maximumFutureSkew, maximumEvents, token, exceptions);
        if (evaluation.CapacityExceeded) throw new ArgumentException("The replay exceeds the event limit.", nameof(samples));
        var results = evaluation.Observations.ToDictionary(x => (x.Event.AgentId, x.Event.MessageId));
        var observations = samples.Select(sample => new SigmaReplayObservation(sample.Event.AgentId, sample.Event.MessageId,
            sample.Expected, results[(sample.Event.AgentId, sample.Event.MessageId)].Result)).ToArray();
        return new(snapshot, rule.Source, observations);
    }

    internal static SigmaReplayResult Run(SigmaRuleSet ruleSet, string path, IReadOnlyList<SigmaReplaySample> samples,
        IReadOnlyList<SigmaRuleException>? exceptions = null)
    {
        var snapshot = ruleSet.Snapshot;
        if (snapshot.Availability != SigmaAvailability.Ready || snapshot.RevisionId is null)
            throw new InvalidOperationException("The rule source is not ready for testing.");
        var rule = ruleSet.Rules.SingleOrDefault(x => string.Equals(x.Source.Path, path, StringComparison.Ordinal))
            ?? throw new ArgumentException("The rule is not available in this source revision.", nameof(path));
        var observations = new SigmaReplayObservation[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var decoded = HostEventDecoder.Decode(sample.Event);
            observations[i] = new(sample.Event.AgentId, sample.Event.MessageId, sample.Expected,
                SigmaExceptions.Apply(rule.Rule.Match(decoded), decoded, exceptions));
        }
        return new(snapshot, rule.Source, observations);
    }
}
