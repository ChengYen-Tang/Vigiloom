using Vigiloom.Contracts.Groups;

namespace Vigiloom.Contracts.Resources;

public sealed record GroupWriteRequest(GroupInput Input, Guid ActorId);
public sealed record GroupMembersRequest(GroupMembershipChange Change, Guid ActorId);
public sealed record GroupDeleteRequest(string? Version, Guid ActorId);
