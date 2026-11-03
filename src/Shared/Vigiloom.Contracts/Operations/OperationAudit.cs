using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Operations;

/// <summary>Versioned, allow-listed audit details; never include arbitrary request data.</summary>
public abstract record OperationAuditDetails;

/// <summary>Non-secret values in a central speed-test setting version.</summary>
public sealed record SpeedSettingsAuditValues(bool Automatic, int IntervalHours, int MaxMiB, int TimeoutSeconds);

/// <summary>Approved version 1 details for a committed speed-test setting change.</summary>
public sealed record SpeedSettingsChanged(
    SpeedSettingsAuditValues Previous, SpeedSettingsAuditValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record PingSettingsAuditValues(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int SuccessThreshold, int MaxConcurrency);
public sealed record PingSettingsChanged(PingSettingsAuditValues Previous, PingSettingsAuditValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record AgentReplayPolicyChanged(AgentReplayPhase Previous, AgentReplayPhase Current,
    DateTimeOffset ActivatedAt, DateTimeOffset GraceUntil, DateTimeOffset? EnforcedAt,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

/// <summary>Approval references only the trusted source; names and credentials are not recorded.</summary>
public sealed record ServiceApproved(Guid SourceId) : OperationAuditDetails;

public enum ServiceSourceAction { Issued = 1, Revoked = 2 }
/// <summary>Records only the action; the credential and source name are never audited.</summary>
public sealed record ServiceSourceChanged(ServiceSourceAction Action) : OperationAuditDetails;

/// <summary>Trusted producer metadata and allow-listed details for one operation.</summary>
public sealed record OperationAudit(Guid ActorId, Guid ResourceId, DateTimeOffset OccurredAt, OperationAuditDetails Details);

/// <summary>A bounded Administrator query of non-secret operation audit events.</summary>
public sealed record OperationAuditQuery(DateTimeOffset From, DateTimeOffset To, int Page = 1);

/// <summary>An event with supported typed details; null details mean an unsupported or invalid stored event.</summary>
public sealed record OperationAuditEntry(DateTimeOffset OccurredAt, Guid ActorId, Guid ResourceId, SpeedSettingsChanged? SpeedSettings, PingSettingsChanged? PingSettings = null, AgentReplayPolicyChanged? AgentReplayPolicy = null, ServiceApproved? ServiceApproval = null, ServiceSourceChanged? ServiceSource = null);

/// <summary>A bounded page of operation audit events.</summary>
public sealed record OperationAuditPage(IReadOnlyList<OperationAuditEntry> Items, int Page, bool HasMore, bool LimitReached);

