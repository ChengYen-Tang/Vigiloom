using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

internal enum IntegrityChangeKind { Added = 1, Modified = 2, Removed = 3 }

internal sealed record IntegritySnapshot(Guid ScopeId, long ScopeRevision, bool Complete, IReadOnlyList<IntegrityItem> Entries);
internal sealed record IntegrityChange(IntegrityChangeKind Kind, IntegrityItem? Before, IntegrityItem? After);
internal sealed record IntegrityComparison(bool NewBaseline, IReadOnlyList<IntegrityChange> Changes);

/// <summary>Compares complete observations only; policy changes create a new baseline.</summary>
internal static class IntegrityChanges
{
    /// <summary>Registry names are case-insensitive, including Unicode represented by percent-encoded UTF-8.</summary>
    internal static StringComparer RegistryKeys { get; } = new RegistryKeyComparer();

    internal static IntegrityComparison Compare(IntegritySnapshot? previous, IntegritySnapshot current, StringComparer keyComparer)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(keyComparer);
        if (current.ScopeId.Version != 7 || current.ScopeRevision < 1) throw new ArgumentException("Invalid integrity scope.", nameof(current));
        if (!current.Complete || previous is { Complete: false }) throw new ArgumentException("Incomplete integrity scans cannot be compared.");
        if (previous is not null && previous.ScopeId != current.ScopeId) throw new ArgumentException("Snapshots belong to different scopes.", nameof(previous));
        if (previous is not null && (previous.ScopeRevision < 1 || previous.ScopeRevision > current.ScopeRevision))
            throw new ArgumentException("A stale scope revision cannot replace a newer baseline.", nameof(current));

        var remaining = new Dictionary<string, IntegrityItem>(keyComparer);
        if (previous is not null && previous.ScopeRevision == current.ScopeRevision)
            foreach (var item in previous.Entries)
                if (!remaining.TryAdd(item.Key, item)) throw new ArgumentException("Duplicate previous integrity key.", nameof(previous));

        var baseline = previous is null || previous.ScopeRevision != current.ScopeRevision;
        var seen = new HashSet<string>(keyComparer);
        var changes = new List<IntegrityChange>();
        foreach (var item in current.Entries)
        {
            if (!seen.Add(item.Key)) throw new ArgumentException("Duplicate current integrity key.", nameof(current));
            if (baseline) continue;
            if (!remaining.Remove(item.Key, out var before)) changes.Add(new(IntegrityChangeKind.Added, null, item));
            else if (before.Kind != item.Kind || before.ContentDigest != item.ContentDigest ||
                     before.SecurityDigest != item.SecurityDigest || before.Owner != item.Owner)
                changes.Add(new(IntegrityChangeKind.Modified, before, item));
        }
        if (!baseline)
            foreach (var item in remaining.Values) changes.Add(new(IntegrityChangeKind.Removed, item, null));
        return new(baseline, changes);
    }

    private sealed class RegistryKeyComparer : StringComparer
    {
        public override int Compare(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Compare(Decode(x), Decode(y));
        public override bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(Decode(x), Decode(y));
        public override int GetHashCode(string value) => StringComparer.OrdinalIgnoreCase.GetHashCode(Decode(value)!);

        // Components cannot contain NUL; this preserves their boundaries even when a name contains '/'.
        private static string? Decode(string? key) => key is null ? null : string.Join('\0', key.Split('/').Select(Uri.UnescapeDataString));
    }
}
