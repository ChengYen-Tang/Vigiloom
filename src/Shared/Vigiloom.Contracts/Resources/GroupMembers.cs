namespace Vigiloom.Contracts.Resources;

/// <summary>Resource identifiers belonging to one group, for internal service queries.</summary>
public sealed record GroupMembers(Guid GroupId, IReadOnlyList<Guid> DeviceIds, IReadOnlyList<Guid> ServiceIds);
