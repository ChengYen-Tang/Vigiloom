using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record SigmaRuleObservation(SigmaSourceRule Source, SigmaMatch Result);
internal sealed record SigmaEventEvaluation(SigmaSnapshot Snapshot, Guid AgentId, Guid MessageId,
    IReadOnlyList<SigmaRuleObservation> Rules);

/// <summary>Decodes each retained event once and evaluates only the selected rule revision.</summary>
internal static class SigmaEvaluator
{
    internal static SigmaEventEvaluation Evaluate(SigmaRuleSet ruleSet, HostLogEntry log, CancellationToken token = default,
        IReadOnlyDictionary<string, IReadOnlyList<SigmaRuleException>>? exceptions = null)
    {
        if (ruleSet.Snapshot.Availability != SigmaAvailability.Ready)
            return new(ruleSet.Snapshot, log.AgentId, log.MessageId, []);

        var decoded = HostEventDecoder.Decode(log);
        var results = new SigmaRuleObservation[ruleSet.Rules.Count];
        for (var i = 0; i < results.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = ruleSet.Rules[i];
            IReadOnlyList<SigmaRuleException>? localExceptions = null;
            exceptions?.TryGetValue(entry.Source.Path, out localExceptions);
            results[i] = new(entry.Source, SigmaExceptions.Apply(entry.Rule.Match(decoded), decoded, localExceptions));
        }
        return new(ruleSet.Snapshot, log.AgentId, log.MessageId, results);
    }
}
