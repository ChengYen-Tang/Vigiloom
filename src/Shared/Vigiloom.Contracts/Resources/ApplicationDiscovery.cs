namespace Vigiloom.Contracts.Resources;

/// <summary>Identity metadata observed by an authenticated telemetry receiver.</summary>
public sealed record ApplicationObservation(Guid SourceId, string ServiceKey, string Environment, string DisplayName, string ServiceNamespace);
public sealed record ApplicationCandidateInfo(Guid Id, Guid SourceId, string ServiceKey, string Environment,
    string DisplayName, string ServiceNamespace, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string SourceName = "");
public sealed record ApplicationSourceStatus(bool Active, string? Name);
public enum ApplicationObservationStatus { Recorded, AlreadyApproved }
public enum ApplicationApprovalStatus { Approved, AlreadyApproved, NotFound, Expired, SourceUnavailable }
public sealed record ApplicationApprovalResult(ApplicationApprovalStatus Status, Guid? ServiceId = null);
public sealed record ApplicationApproved(Guid ServiceId, Guid SourceId, string ServiceKey, string Environment,
    string DisplayName, string ServiceNamespace, DateTimeOffset ApprovedAt, byte[] Version);
public sealed record ApplicationApprovalAudit(Guid ActorId, Guid ServiceId, Guid SourceId, DateTimeOffset OccurredAt);
public sealed record ApplicationApprovalRequest(Guid ActorId);
