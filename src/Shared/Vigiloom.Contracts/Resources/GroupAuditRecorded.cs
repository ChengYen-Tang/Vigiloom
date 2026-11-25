using Vigiloom.Contracts.Operations;

namespace Vigiloom.Contracts.Resources;

/// <summary>Non-secret group operation delivered to the platform audit service.</summary>
public sealed record GroupAuditRecorded(Guid ActorId, Guid GroupId, DateTimeOffset OccurredAt, GroupAction Action, int Added, int Removed);
