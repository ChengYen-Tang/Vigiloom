using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Inventory;

/// <summary>The outcome of one inventory source, independent of other sources on the host.</summary>
public enum InventoryReadStatus { Complete = 1, Partial = 2, PermissionDenied = 3, Unsupported = 4, Error = 5 }

/// <summary>Only a complete source read may replace its last complete snapshot.</summary>
public sealed record InventoryReadResult<T>(InventoryReadStatus Status, IReadOnlyList<T> Items, InventoryPartialReason? PartialReason = null);

/// <summary>A source-native installed software entry; registry view is not executable architecture.</summary>
public sealed record InstalledSoftware(string SourceKey, string Name, string? Version, string? Supplier, string? Architecture = null);

/// <summary>Locally configured principal; credentials and password fields are never collected.</summary>
public sealed record LocalPrincipal(string Name, uint Id, uint? PrimaryGroupId, IReadOnlyList<string> Members);

/// <summary>An installed update observed from a named source, with its original date text.</summary>
public sealed record InstalledPatch(string SourceKey, string? Description, string? InstalledOn);
