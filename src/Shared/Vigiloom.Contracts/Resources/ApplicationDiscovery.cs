namespace Vigiloom.Contracts.Resources;

/// <summary>Identity metadata observed by an authenticated telemetry receiver.</summary>
public sealed record ApplicationObservation(Guid SourceId, string ServiceKey, string Environment, string DisplayName, string ServiceNamespace);
public sealed record ApplicationCandidateInfo(Guid Id, Guid SourceId, string ServiceKey, string Environment,
    string DisplayName, string ServiceNamespace, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string SourceName = "");
public sealed record ApplicationSourceStatus(bool Active, string? Name);
public enum ApplicationObservationStatus { Recorded, AlreadyApproved, CapacityReached }
public enum ApplicationApprovalStatus { Approved, AlreadyApproved, NotFound, Expired, SourceUnavailable }
public sealed record ApplicationApprovalResult(ApplicationApprovalStatus Status, Guid? ServiceId = null);
public sealed record ApplicationApproved(Guid ServiceId, Guid SourceId, string ServiceKey, string Environment,
    string DisplayName, string ServiceNamespace, DateTimeOffset ApprovedAt, byte[] Version);
public sealed record ApplicationApprovalAudit(Guid ActorId, Guid ServiceId, Guid SourceId, DateTimeOffset OccurredAt);
public sealed record ApplicationApprovalRequest(Guid ActorId);
public sealed record ApplicationStateChange(string Version, string Action, Guid ActorId);
public enum ApplicationStateChangeStatus { Updated, NotFound, Conflict, SourceUnavailable }
public sealed record ApplicationStateChanged(Guid ServiceId, DateTimeOffset? StoppedAt, DateTimeOffset? ArchivedAt, byte[] Version);
public sealed record ApplicationStateAudit(Guid ActorId, Guid ServiceId, string Action, DateTimeOffset OccurredAt);
