namespace Vigiloom.Contracts.Devices;

/// <summary>Device fields supplied by an administrator. Additional targets share the same device identity.</summary>
public sealed record DeviceInput(string? Name, string? Address, string[]? AdditionalTargets, bool DeepMonitoring, bool Enabled = true, string? Version = null, bool Public = false);

/// <summary>A device visible to authenticated monitoring users.</summary>
public sealed record DeviceDetails(Guid Id, string Name, string Address, IReadOnlyList<string> AdditionalTargets,
    bool DeepMonitoring, bool Enabled, bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Version);

/// <summary>A page of devices ordered by an explicitly selected field.</summary>
public sealed record DevicePage(IReadOnlyList<DeviceDetails> Items, int Page, int PageSize, int Total);

/// <summary>Field-level validation failures for a device operation.</summary>
public sealed record DeviceValidationError(IReadOnlyDictionary<string, string[]> Errors);
