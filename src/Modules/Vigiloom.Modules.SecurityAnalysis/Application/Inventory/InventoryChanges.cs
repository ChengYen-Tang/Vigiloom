using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

internal enum InventoryChangeKind { Added = 1, Modified = 2, Removed = 3, NewlyObserved = 4, ObservedModified = 5 }

internal sealed record InventoryChange(InventoryChangeKind Kind, InventoryEntry? Before, InventoryEntry? After);

/// <summary>Compares two verified, complete snapshots of the same Agent and source.</summary>
internal static class InventoryChanges
{
    internal static IReadOnlyList<InventoryChange> Compare(
        IEnumerable<InventoryEntry> previous, IEnumerable<InventoryEntry> current)
    {
        // Source keys are case-sensitive even when the SQL Server collation is not.
        var remaining = new Dictionary<string, InventoryEntry>(StringComparer.Ordinal);
        foreach (var entry in previous)
            if (!remaining.TryAdd(entry.Key, entry))
                throw new ArgumentException("The previous snapshot contains duplicate keys.", nameof(previous));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<InventoryChange>();
        foreach (var entry in current)
        {
            if (!seen.Add(entry.Key))
                throw new ArgumentException("The current snapshot contains duplicate keys.", nameof(current));
            if (!remaining.Remove(entry.Key, out var before))
                changes.Add(new(InventoryChangeKind.Added, null, entry));
            else if (!SameContent(before, entry))
                changes.Add(new(InventoryChangeKind.Modified, before, entry));
        }
        foreach (var entry in remaining.Values)
            changes.Add(new(InventoryChangeKind.Removed, entry, null));
        return changes;
    }

    // ProcessStartedAt is display-only: Linux derives wall time from a boot-time
    // estimate that can shift across Agent restarts; the stable key holds identity.
    private static bool SameContent(InventoryEntry left, InventoryEntry right) =>
        left.Name == right.Name && left.Version == right.Version && left.Architecture == right.Architecture &&
        left.Supplier == right.Supplier && left.NumericId == right.NumericId && left.PrimaryGroupId == right.PrimaryGroupId &&
        SameMembers(left.Members, right.Members) && left.InstalledOn == right.InstalledOn &&
        left.OperatingSystem == right.OperatingSystem && left.ProcessArchitecture == right.ProcessArchitecture &&
        left.Runtime == right.Runtime && left.AvailableProcessorCount == right.AvailableProcessorCount &&
        left.OperatingSystemFamily == right.OperatingSystemFamily && left.DistributionId == right.DistributionId &&
        left.DistributionVersion == right.DistributionVersion && left.BootedAt == right.BootedAt && left.AgentVersion == right.AgentVersion &&
        left.TotalMemoryBytes == right.TotalMemoryBytes && left.ResourceType == right.ResourceType &&
        left.ParentKey == right.ParentKey && left.Address == right.Address &&
        left.CapacityBytes == right.CapacityBytes && left.HardwareType == right.HardwareType &&
        left.ParentProcessId == right.ParentProcessId &&
        left.RunAs == right.RunAs && left.ExecutablePath == right.ExecutablePath && left.CommandLine == right.CommandLine &&
        left.ServiceStatus == right.ServiceStatus && left.StartupMode == right.StartupMode && left.Protocol == right.Protocol &&
        left.Port == right.Port && left.OwningProcessId == right.OwningProcessId && left.OwningProcessStartedAt == right.OwningProcessStartedAt;

    private static bool SameMembers(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);
    }
}
