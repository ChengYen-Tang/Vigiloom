namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record CorrelationCandidate(string? Id, string? Name, SigmaCompiledEntry? Rule);
internal enum CorrelationReferenceStatus { Ready, Missing, Ambiguous, Unsupported }
internal sealed record CorrelationReferenceResult(CorrelationReferenceStatus Status, SigmaCompiledEntry? Rule = null);

/// <summary>Resolves against all source candidates, including rules the local matcher cannot execute.</summary>
internal static class CorrelationReference
{
    internal static CorrelationReferenceResult Resolve(string reference, IReadOnlyList<CorrelationCandidate> candidates)
    {
        CorrelationCandidate? match = null;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Id, reference, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.Name, reference, StringComparison.Ordinal)) continue;
            if (match is not null) return new(CorrelationReferenceStatus.Ambiguous);
            match = candidate;
        }
        return match is null ? new(CorrelationReferenceStatus.Missing) : match.Rule is null
            ? new(CorrelationReferenceStatus.Unsupported) : new(CorrelationReferenceStatus.Ready, match.Rule);
    }
}
