namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum OsvEventKind { Introduced, Fixed, LastAffected, Limit }
internal readonly record struct OsvVersionEvent(OsvEventKind Kind, string Version);

/// <summary>Evaluates one Ubuntu ECOSYSTEM range using Debian ordering and explicit boundaries.</summary>
internal static class UbuntuOsvRange
{
    internal static MatchState Evaluate(string? version, IReadOnlyList<OsvVersionEvent> events)
    {
        if (!DebianVersion.TryCompare(version, version, out _) || events.Count is 0 or > 256) return MatchState.Unknown;
        var ordered = new OsvVersionEvent[events.Count];
        var introduced = false;
        var hasFixed = false;
        var hasLastAffected = false;
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (item.Kind is not (OsvEventKind.Introduced or OsvEventKind.Fixed or OsvEventKind.LastAffected) ||
                item.Version is null || item.Version == "0" && item.Kind != OsvEventKind.Introduced ||
                item.Version != "0" && !DebianVersion.TryCompare(item.Version, item.Version, out _)) return MatchState.Unknown;
            introduced |= item.Kind == OsvEventKind.Introduced;
            hasFixed |= item.Kind == OsvEventKind.Fixed;
            hasLastAffected |= item.Kind == OsvEventKind.LastAffected;
            ordered[i] = item;
        }
        if (!introduced || hasFixed && hasLastAffected) return MatchState.Unknown;
        Array.Sort(ordered, Compare);
        var active = false;
        var activeHasClosing = false;
        var fixedBefore = false;
        var open = false;
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            if (i != 0 && CompareVersion(ordered[i - 1].Version, item.Version) == 0) return MatchState.Unknown;
            if (item.Kind == OsvEventKind.Introduced)
            {
                if (open) return MatchState.Unknown;
                open = true;
            }
            else
            {
                if (!open) return MatchState.Unknown;
                open = false;
            }
            var relative = item.Version == "0" && item.Kind == OsvEventKind.Introduced ? 1 : CompareInstalledVersion(version!, item.Version);
            if (relative < 0) continue;
            if (item.Kind == OsvEventKind.Introduced) { active = true; activeHasClosing = i + 1 < ordered.Length && ordered[i + 1].Kind != OsvEventKind.Introduced; }
            else if (item.Kind == OsvEventKind.Fixed) { active = false; fixedBefore = true; }
            else if (relative > 0) active = false;
        }
        // Ubuntu OSV uses an open range for both known issues and needs-triage.
        if (active) return activeHasClosing ? MatchState.Affected : MatchState.Unknown;
        return fixedBefore ? MatchState.Fixed : MatchState.NotAffected;
    }

    private static int Compare(OsvVersionEvent left, OsvVersionEvent right) => CompareVersion(left.Version, right.Version);
    private static int CompareInstalledVersion(string left, string right) =>
        DebianVersion.TryCompare(left, right, out var order) ? order : throw new InvalidOperationException("Versions were not validated.");
    private static int CompareVersion(string left, string right)
    {
        if (left == "0") return right == "0" ? 0 : -1;
        if (right == "0") return 1;
        return DebianVersion.TryCompare(left, right, out var order) ? order : throw new InvalidOperationException("Versions were not validated.");
    }
}
