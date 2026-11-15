namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum UbuntuFixStatus { Available, NoConfirmedFix, EvidenceUnavailable, NotAffected, LimitExceeded }
internal sealed record UbuntuFixResult(UbuntuFixStatus Status, IReadOnlyList<string> Versions, long Work = 0);

/// <summary>Finds source-package fixed boundaries and rechecks each against the complete scoped advisory.</summary>
internal static class UbuntuFixCandidates
{
    private const int MaximumCandidates = 256;
    private const long MaximumWork = 10_000_000;

    internal static UbuntuFixResult Find(UbuntuOsvEvidence inventory, IReadOnlyList<UbuntuOsvAdvisory> advisories, CancellationToken token, long maximumWork = MaximumWork)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(advisories);
        maximumWork = Math.Min(maximumWork, MaximumWork);
        if (maximumWork <= 0) return new(UbuntuFixStatus.LimitExceeded, []);
        var relevant = advisories.Where(x => string.Equals(x.Ecosystem, inventory.Ecosystem, StringComparison.Ordinal) &&
            string.Equals(x.SourcePackage, inventory.SourcePackage, StringComparison.Ordinal)).ToArray();
        if (relevant.Length == 0) return new(UbuntuFixStatus.EvidenceUnavailable, []);
        var candidates = new SortedSet<string>(Comparer<string>.Create((a, b) =>
            DebianVersion.TryCompare(a, b, out var order) ? order : throw new InvalidDataException("Invalid fixed version.")));
        long work = 0;
        foreach (var advisory in relevant)
        {
            token.ThrowIfCancellationRequested();
            work += advisory.Versions.Count;
            if (work > maximumWork) return new(UbuntuFixStatus.LimitExceeded, []);
            foreach (var range in advisory.Ranges)
            {
                work += range.Events.Count;
                if (work > maximumWork) return new(UbuntuFixStatus.LimitExceeded, []);
                foreach (var item in range.Events)
                {
                    if (range.Type != "ECOSYSTEM" || item.Kind != OsvEventKind.Fixed ||
                        !DebianVersion.TryCompare(item.Version, inventory.SourceVersion, out var order) || order <= 0) continue;
                    candidates.Add(item.Version);
                    if (candidates.Count > MaximumCandidates) return new(UbuntuFixStatus.LimitExceeded, []);
                }
            }
        }
        if (work > maximumWork / (candidates.Count + 1L)) return new(UbuntuFixStatus.LimitExceeded, []);
        var estimatedWork = work * (candidates.Count + 1L);
        var current = Aggregate(inventory);
        if (current.Uncertain || current.State is MatchState.Unknown or MatchState.Unsupported or MatchState.NotEvaluated) return new(UbuntuFixStatus.EvidenceUnavailable, [], estimatedWork);
        if (current.State != MatchState.Affected) return new(UbuntuFixStatus.NotAffected, [], estimatedWork);
        var accepted = new List<string>();
        var uncertain = false;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var evaluated = Aggregate(inventory with { SourceVersion = candidate });
            uncertain |= evaluated.Uncertain;
            if (!evaluated.Uncertain && evaluated.State == MatchState.Fixed) accepted.Add(candidate);
        }
        return new(accepted.Count > 0 ? UbuntuFixStatus.Available : uncertain ? UbuntuFixStatus.EvidenceUnavailable : UbuntuFixStatus.NoConfirmedFix, accepted, estimatedWork);

        (MatchState State, bool Uncertain) Aggregate(UbuntuOsvEvidence evidence)
        {
            var states = new MatchState[relevant.Length];
            for (var i = 0; i < relevant.Length; i++)
                states[i] = UbuntuOsvMatch.EvaluateCancellable(evidence, relevant[i], token).State;
            return (UbuntuPackageEvaluation.Aggregate(states), states.Any(x => x is MatchState.Unknown or MatchState.Unsupported or MatchState.NotEvaluated));
        }
    }
}
