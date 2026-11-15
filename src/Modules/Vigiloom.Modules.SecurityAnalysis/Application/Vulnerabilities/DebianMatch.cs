namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum DebianAdvisoryState { Undetermined, FixedVersion, Open, NotAffected }
internal enum MatchReason { VersionBelowFix, VersionMeetsFix, OpenWithoutFix, VendorNotAffected,
    MissingEvidence, DifferentScope, ArchitectureNotApplicable, InvalidVersion, VendorUndetermined, OriginConflict, InvalidArchitectureScope }

internal sealed record DebianInventoryEvidence(string? Release, string? SourcePackage, string? SourceVersion,
    string? Architecture, bool CompleteAndCurrent, PackageOrigin Origin);

internal sealed record DebianAdvisoryEvidence(string Release, string SourcePackage, DebianAdvisoryState State,
    string? FixedVersion, IReadOnlySet<string>? Architectures, bool CompleteAndCurrent);
internal readonly record struct DebianMatchResult(MatchState State, MatchReason Reason, PackageOrigin Origin);

/// <summary>Compares one already-discovered, release-specific vendor advisory against package inventory.</summary>
internal static class DebianMatch
{
    internal static DebianMatchResult Evaluate(DebianInventoryEvidence inventory, DebianAdvisoryEvidence advisory)
    {
        if (!inventory.CompleteAndCurrent || !advisory.CompleteAndCurrent || string.IsNullOrWhiteSpace(inventory.Release) ||
            string.IsNullOrWhiteSpace(inventory.SourcePackage) || string.IsNullOrWhiteSpace(inventory.SourceVersion) ||
            string.IsNullOrWhiteSpace(inventory.Architecture)) return Result(MatchState.Unknown, MatchReason.MissingEvidence);
        if (inventory.Origin is not (PackageOrigin.AssumedDistribution or PackageOrigin.VerifiedDistribution))
            return Result(MatchState.Unknown, MatchReason.OriginConflict);
        if (!string.Equals(inventory.Release, advisory.Release, StringComparison.Ordinal) ||
            !string.Equals(inventory.SourcePackage, advisory.SourcePackage, StringComparison.Ordinal))
            return Result(MatchState.Unknown, MatchReason.DifferentScope);
        if (!DebianVersion.TryCompare(inventory.SourceVersion, inventory.SourceVersion, out _))
            return Result(MatchState.Unknown, MatchReason.InvalidVersion);
        if (advisory.Architectures is { } architectures)
        {
            if (architectures.Count == 0) return Result(MatchState.Unknown, MatchReason.InvalidArchitectureScope);
            if (!architectures.Contains(inventory.Architecture)) return Result(MatchState.NotAffected, MatchReason.ArchitectureNotApplicable);
        }
        return advisory.State switch
        {
            DebianAdvisoryState.NotAffected => Result(MatchState.NotAffected, MatchReason.VendorNotAffected),
            DebianAdvisoryState.Open => Result(MatchState.Affected, MatchReason.OpenWithoutFix),
            DebianAdvisoryState.FixedVersion when DebianVersion.TryCompare(inventory.SourceVersion, advisory.FixedVersion, out var order) =>
                order < 0 ? Result(MatchState.Affected, MatchReason.VersionBelowFix) : Result(MatchState.Fixed, MatchReason.VersionMeetsFix),
            DebianAdvisoryState.FixedVersion => Result(MatchState.Unknown, MatchReason.InvalidVersion),
            _ => Result(MatchState.Unknown, MatchReason.VendorUndetermined)
        };

        DebianMatchResult Result(MatchState state, MatchReason reason) => new(state, reason, inventory.Origin);
    }
}
