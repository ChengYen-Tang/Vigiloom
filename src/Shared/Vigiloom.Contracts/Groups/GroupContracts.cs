namespace Vigiloom.Contracts.Groups;

/// <summary>A group is an organizational label; it does not alter visibility or monitoring.</summary>
public sealed record GroupInput(string? Name, string? Description, string? Version = null);
public sealed record GroupDetails(Guid Id, string Name, string? Description, int DeviceCount, int ArchivedDeviceCount, int ServiceCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Version);
public sealed record GroupPage(IReadOnlyList<GroupDetails> Items, int Page, int PageSize, int Total);
public sealed record GroupDevice(Guid Id, string Name, string Address, bool DeepMonitoring, bool Enabled, bool IsMember);
public sealed record GroupDevicePage(IReadOnlyList<GroupDevice> Items, int Page, int PageSize, int Total);
public sealed record GroupService(Guid Id, string Name, string Environment, bool IsMember);
public sealed record GroupServicePage(IReadOnlyList<GroupService> Items, int Page, int PageSize, int Total);
public sealed record GroupMembershipChange(string? Version, Guid[]? Add, Guid[]? Remove);
public sealed record GroupVersionRequest(string? Version);
