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

public sealed record SpeedRetentionChanged(int PreviousDays, int CurrentDays,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record PingSettingsAuditValues(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int SuccessThreshold, int MaxConcurrency);
public sealed record PingSettingsChanged(PingSettingsAuditValues Previous, PingSettingsAuditValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record AgentReplayPolicyChanged(AgentReplayPhase Previous, AgentReplayPhase Current,
    DateTimeOffset ActivatedAt, DateTimeOffset GraceUntil, DateTimeOffset? EnforcedAt,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record AgentInboxSettingsChanged(int PreviousReplaySeconds, int PreviousRetentionDays,
    int CurrentReplaySeconds, int CurrentRetentionDays, string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public sealed record AgentInboxCleanupActivated(DateTimeOffset CutoverAt, DateTimeOffset LegacyRetainUntil,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

/// <summary>Approval references only the trusted source; names and credentials are not recorded.</summary>
public sealed record ServiceApproved(Guid SourceId) : OperationAuditDetails;

public enum ServiceSourceAction { Issued = 1, Revoked = 2, CredentialRotated = 3 }
/// <summary>Records only the action; the credential and source name are never audited.</summary>
public sealed record ServiceSourceChanged(ServiceSourceAction Action) : OperationAuditDetails;

public sealed record InstancePolicyValues(int PerService, int Total);
public sealed record InstancePolicyChanged(InstancePolicyValues Previous, InstancePolicyValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;
public sealed record LogQuotaAuditValues(int SourceRate, int SourceBurst, int GlobalRate, int GlobalBurst);
public sealed record LogQuotaChanged(LogQuotaAuditValues Previous, LogQuotaAuditValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;
public sealed record TraceQuotaChanged(LogQuotaAuditValues Previous, LogQuotaAuditValues Current,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;
public enum ServiceInstanceAction { Retired = 1, Restored = 2 }
public sealed record ServiceInstanceChanged(ServiceInstanceAction Action, Guid ServiceId) : OperationAuditDetails;
public enum ServiceCollectionAction { Stopped = 1, Resumed = 2 }
public sealed record ServiceCollectionChanged(ServiceCollectionAction Action) : OperationAuditDetails;
public enum CollectorAction { Registered = 1, RouteAdded = 2, RouteRemoved = 3 }
/// <summary>Records identifiers only, never credentials or arbitrary exporter text.</summary>
public sealed record CollectorChanged(CollectorAction Action, Guid MonitoringServiceId, Guid? SourceId) : OperationAuditDetails;

public enum HealthTargetAction { Created = 1, Updated = 2 }
public sealed record HealthTargetAuditValues(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int RecoveryThreshold, bool Enabled);
/// <summary>Records non-secret settings and versions, never the endpoint or query string.</summary>
public sealed record HealthTargetChanged(HealthTargetAction Action, Guid ServiceId, HealthTargetAuditValues? Previous,
    HealthTargetAuditValues Current, string PreviousVersion, string CurrentVersion, bool EndpointChanged) : OperationAuditDetails;
/// <summary>References immutable network policy revisions without duplicating address ranges in the audit feed.</summary>
public sealed record HealthNetworkPolicyChanged(long PreviousGeneration, long CurrentGeneration,
    string PreviousVersion, string CurrentVersion) : OperationAuditDetails;

public enum GroupAction { Created = 1, Updated = 2, Deleted = 3, DevicesChanged = 4, ServicesChanged = 5 }
/// <summary>Records a group operation without names, addresses or service metadata.</summary>
public sealed record GroupChanged(GroupAction Action, int Added, int Removed) : OperationAuditDetails;

/// <summary>Trusted producer metadata and allow-listed details for one operation.</summary>
public sealed record OperationAudit(Guid ActorId, Guid ResourceId, DateTimeOffset OccurredAt, OperationAuditDetails Details);

/// <summary>A bounded Administrator query of non-secret operation audit events.</summary>
public sealed record OperationAuditQuery(DateTimeOffset From, DateTimeOffset To, int Page = 1);

/// <summary>An event with supported typed details; null details mean an unsupported or invalid stored event.</summary>
public sealed record OperationAuditEntry(DateTimeOffset OccurredAt, Guid ActorId, Guid ResourceId, SpeedSettingsChanged? SpeedSettings, PingSettingsChanged? PingSettings = null, AgentReplayPolicyChanged? AgentReplayPolicy = null, ServiceApproved? ServiceApproval = null, ServiceSourceChanged? ServiceSource = null, GroupChanged? Group = null, InstancePolicyChanged? InstancePolicy = null, ServiceInstanceChanged? ServiceInstance = null, ServiceCollectionChanged? ServiceCollection = null, LogQuotaChanged? LogQuota = null, TraceQuotaChanged? TraceQuota = null, CollectorChanged? Collector = null, HealthTargetChanged? HealthTarget = null, HealthNetworkPolicyChanged? HealthNetworkPolicy = null, SpeedRetentionChanged? SpeedRetention = null, AgentInboxSettingsChanged? AgentInboxSettings = null, AgentInboxCleanupActivated? AgentInboxActivation = null);

/// <summary>A bounded page of operation audit events.</summary>
public sealed record OperationAuditPage(IReadOnlyList<OperationAuditEntry> Items, int Page, bool HasMore, bool LimitReached);

