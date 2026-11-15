using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record CorrelationPlan(EventCountRule Rule, SigmaCompiledEntry BaseRule);
internal sealed record CorrelationPlanResult(CorrelationPlan? Plan, string? Error);

/// <summary>Resolves a supported correlation against one verified source revision.</summary>
internal static class CorrelationPlanner
{
    internal static CorrelationPlanResult Create(ReadOnlySpan<byte> content, SigmaRuleSet rules, TimeSpan maximumWindow)
    {
        if (rules.Snapshot.Availability != SigmaAvailability.Ready || rules.Snapshot.RevisionId is null)
            return new(null, "The rule source is unavailable.");

        var compiled = CorrelationCompiler.Compile(content, maximumWindow);
        if (compiled.Rule is not { } correlation) return new(null, compiled.Error);

        return Resolve(correlation, rules);
    }

    internal static CorrelationPlanResult Resolve(EventCountRule correlation, SigmaRuleSet rules)
    {
        if (rules.Snapshot.Availability != SigmaAvailability.Ready || rules.Snapshot.RevisionId is null)
            return new(null, "The rule source is unavailable.");
        var reference = CorrelationReference.Resolve(correlation.Reference, rules.References);
        return reference.Status switch
        {
            CorrelationReferenceStatus.Ready when reference.Rule is { } baseRule => new(new(correlation, baseRule), null),
            CorrelationReferenceStatus.Missing => new(null, "The referenced rule was not found in this source revision."),
            CorrelationReferenceStatus.Ambiguous => new(null, "The referenced rule is ambiguous in this source revision."),
            _ => new(null, "The referenced rule is not supported.")
        };
    }
}
