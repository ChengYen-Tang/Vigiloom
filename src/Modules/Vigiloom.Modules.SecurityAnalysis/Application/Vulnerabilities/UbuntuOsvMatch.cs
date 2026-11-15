namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record UbuntuOsvEvidence(string Ecosystem, string SourcePackage, string SourceVersion,
    PackageOrigin Origin, bool CompleteAndCurrent);
internal sealed record UbuntuOsvRangeEvidence(string Type, IReadOnlyList<OsvVersionEvent> Events);
internal sealed record UbuntuOsvAdvisory(string Ecosystem, string SourcePackage, bool Withdrawn,
    bool CompleteAndCurrent, IReadOnlyList<UbuntuOsvRangeEvidence> Ranges, IReadOnlyList<string> Versions);
internal readonly record struct UbuntuOsvResult(MatchState State, PackageOrigin Origin);

/// <summary>Combines all ranges for one Ubuntu source package; incomplete or conflicting evidence stays unknown.</summary>
internal static class UbuntuOsvMatch
{
    internal static UbuntuOsvResult Evaluate(UbuntuOsvEvidence inventory, UbuntuOsvAdvisory advisory) =>
        EvaluateCancellable(inventory, advisory, CancellationToken.None);

    internal static UbuntuOsvResult EvaluateCancellable(UbuntuOsvEvidence inventory, UbuntuOsvAdvisory advisory, CancellationToken token)
    {
        var unknown = new UbuntuOsvResult(MatchState.Unknown, inventory.Origin);
        if (!inventory.CompleteAndCurrent || !advisory.CompleteAndCurrent || advisory.Withdrawn ||
            inventory.Origin is not (PackageOrigin.AssumedDistribution or PackageOrigin.VerifiedDistribution) ||
            string.IsNullOrWhiteSpace(inventory.SourcePackage) || string.IsNullOrWhiteSpace(inventory.Ecosystem) ||
            !inventory.Ecosystem.StartsWith("Ubuntu:", StringComparison.Ordinal) ||
            !string.Equals(inventory.Ecosystem, advisory.Ecosystem, StringComparison.Ordinal) ||
            !string.Equals(inventory.SourcePackage, advisory.SourcePackage, StringComparison.Ordinal) ||
            !DebianVersion.TryCompare(inventory.SourceVersion, inventory.SourceVersion, out _)) return unknown;

        if (advisory.Ranges.Count == 0 || advisory.Ranges.Count > 128 || advisory.Versions.Count > 100_000) return unknown;
        var listed = false;
        var examined = 0;
        foreach (var version in advisory.Versions)
        {
            if ((examined++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!DebianVersion.TryCompare(inventory.SourceVersion, version, out var order)) return unknown;
            listed |= order == 0;
        }
        var affected = false;
        var hasUnknown = false;
        var fixedRange = false;
        foreach (var range in advisory.Ranges)
        {
            token.ThrowIfCancellationRequested();
            var state = range.Type == "ECOSYSTEM" ? UbuntuOsvRange.Evaluate(inventory.SourceVersion, range.Events) : MatchState.Unknown;
            affected |= state == MatchState.Affected;
            fixedRange |= state == MatchState.Fixed;
            hasUnknown |= state == MatchState.Unknown;
        }
        if (affected) return new(MatchState.Affected, inventory.Origin);
        if (hasUnknown || listed) return unknown;
        return new(fixedRange ? MatchState.Fixed : MatchState.NotAffected, inventory.Origin);
    }
}
