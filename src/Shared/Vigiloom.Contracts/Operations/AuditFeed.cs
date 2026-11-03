namespace Vigiloom.Contracts.Operations;

/// <summary>Safe, bounded metadata from a single owning audit source.</summary>
public enum AuditFeedSource { Accounts = 1, ApiCredentials = 2, Devices = 3, AgentIdentity = 4, AgentSettings = 5, Scheduling = 6 }

public enum AuditFeedAction
{
    Unknown, AccountCreated, AccountRenamed, AccountRoleChanged, AccountDeleted, PasswordResetIssued,
    PasswordResetCompleted, PasswordChanged, TwoFactorEnrollmentStarted, TwoFactorEnabled, TwoFactorDisabled,
    TwoFactorResetByAdministrator, InitialAdministratorCreated, LocalAdministratorRecovery,
    ApiCredentialIssued, ApiCredentialRevoked,
    DeviceCreated, DeviceUpdated, DeviceArchived, DeviceRestored,
    EnrollmentIssued, EnrollmentRevoked, AgentEnrolled, AgentRevoked, RotationStarted, RotationFinished,
    AgentSettingsChanged, ScheduleSettingsChanged, JobDeleteRequested, JobRequeueRequested, JobEnqueueRequested,
    RecurringJobRemoveRequested, RecurringJobTriggerRequested
}

public enum AuditFeedOutcome { Unknown, Recorded, Started, Accepted, Pending, Rejected }
public sealed record AgentSettingsAuditDetails(int? HeartbeatSeconds, int? SettingsPollSeconds, int? BatchIntervalSeconds,
    int EffectiveHeartbeatSeconds, int EffectiveSettingsPollSeconds, int EffectiveBatchIntervalSeconds, string Revision, string Version);
public sealed record AuditFeedEntry(DateTimeOffset OccurredAt, Guid? ActorId, Guid? SubjectId, AuditFeedAction Action, AuditFeedOutcome Outcome,
    AgentSettingsAuditDetails? AgentSettings = null, string? Reference = null, Guid? AgentId = null);
public sealed record AuditFeedPage(AuditFeedSource Source, IReadOnlyList<AuditFeedEntry> Items, int Page, bool HasMore, bool LimitReached);

public static class AuditFeedLimits
{
    public const int PageSize = 50;
    public static bool Valid(OperationAuditQuery query) => query.From.Offset == TimeSpan.Zero && query.To.Offset == TimeSpan.Zero &&
        query.To > query.From && query.To - query.From <= TimeSpan.FromDays(31) && query.Page is >= 1 and <= 1000;
}
