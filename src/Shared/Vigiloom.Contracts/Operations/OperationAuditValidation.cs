using System.Text.Json;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Services;

namespace Vigiloom.Contracts.Operations;

/// <summary>Validates and encodes approved audit details consistently at source and destination.</summary>
public static class OperationAuditValidation
{
    public static (short Kind, string Details) Encode(OperationAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.ActorId == Guid.Empty || audit.ResourceId == Guid.Empty || audit.OccurredAt.Offset != TimeSpan.Zero || audit.OccurredAt == default)
            throw new ArgumentException("Invalid audit event metadata.", nameof(audit));

        var (kind, details) = audit.Details switch
        {
            EventTimelineAccessed access when IsValid(access) => (AuditKinds.EventTimelineAccessedV1, JsonSerializer.Serialize(access)),
            EventEvidenceAccessed access when IsValid(access) => (AuditKinds.EventEvidenceAccessedV1, JsonSerializer.Serialize(access)),
            SpeedSettingsChanged change when IsValid(change) => (AuditKinds.SpeedSettingsChangedV1, JsonSerializer.Serialize(change)),
            SpeedRetentionChanged change when IsValid(change) => (AuditKinds.SpeedRetentionChangedV1, JsonSerializer.Serialize(change)),
            AuditRetentionChanged change when IsValid(change) => (AuditKinds.AuditRetentionChangedV1, JsonSerializer.Serialize(change)),
            HostMetricRetentionChanged change when IsValid(change) => (AuditKinds.HostMetricRetentionChangedV1, JsonSerializer.Serialize(change)),
            TelemetryRetentionChanged change when IsValid(change) => (AuditKinds.TelemetryRetentionChangedV1, JsonSerializer.Serialize(change)),
            PingSettingsChanged change when IsValid(change) => (AuditKinds.PingSettingsChangedV1, JsonSerializer.Serialize(change)),
            AgentReplayPolicyChanged change when IsValid(change) => (AuditKinds.AgentReplayPolicyChangedV1, JsonSerializer.Serialize(change)),
            AgentInboxSettingsChanged change when IsValid(change) => (AuditKinds.AgentInboxSettingsChangedV1, JsonSerializer.Serialize(change)),
            AgentInboxCleanupActivated change when IsValid(change) => (AuditKinds.AgentInboxCleanupActivatedV1, JsonSerializer.Serialize(change)),
            AgentPackageSourceChanged change when IsValid(change) => (AuditKinds.AgentPackageSourceChangedV1, JsonSerializer.Serialize(change)),
            AgentPackageCatalogueRefreshed change when IsValid(change) => (AuditKinds.AgentPackageCatalogueRefreshedV1, JsonSerializer.Serialize(change)),
            AgentPackageTrustInitialized change when IsValid(change) => (AuditKinds.AgentPackageTrustInitializedV1, JsonSerializer.Serialize(change)),
            AgentRegistrationRecorded change when IsValid(change) => (AuditKinds.AgentRegistrationRecordedV1, JsonSerializer.Serialize(change)),
            AgentVersionAssigned change when IsValid(change) => (AuditKinds.AgentVersionAssignedV1, JsonSerializer.Serialize(change)),
            IntelligenceRevisionActivated change when IsValid(change) => (AuditKinds.IntelligenceRevisionActivatedV1, JsonSerializer.Serialize(change)),
            IntelligenceRevisionRevoked change when change.RevisionId.Version == 7 => (AuditKinds.IntelligenceRevisionRevokedV1, JsonSerializer.Serialize(change)),
            SigmaRuleStateChanged change when change.TestId.Version == 7 => (AuditKinds.SigmaRuleStateChangedV1, JsonSerializer.Serialize(change)),
            BaselineExceptionRecorded change when IsValid(change) => (AuditKinds.BaselineExceptionRecordedV1, JsonSerializer.Serialize(change)),
            LineSettingsChanged change when change.Action is LineAuditAction.Channel or LineAuditAction.Group => (AuditKinds.LineSettingsChangedV1, JsonSerializer.Serialize(change)),
            IntelligenceOfflineImported change when change.RevisionId.Version == 7 => (AuditKinds.IntelligenceOfflineImportedV1, JsonSerializer.Serialize(change)),
            IntelligencePolicyChanged change when IsValid(change) => (AuditKinds.IntelligencePolicyChangedV1, JsonSerializer.Serialize(change)),
            AdvisoryImportAbandoned change when IsValid(change) => (AuditKinds.AdvisoryImportAbandonedV1, JsonSerializer.Serialize(change)),
            AdvisoryCredentialChanged change => (AuditKinds.AdvisoryCredentialChangedV1, JsonSerializer.Serialize(change)),
            ServiceApproved change when change.SourceId.Version == 7 => (AuditKinds.ServiceApprovedV1, JsonSerializer.Serialize(change)),
            ServiceSourceChanged change when change.Action is ServiceSourceAction.Issued or ServiceSourceAction.Revoked or ServiceSourceAction.CredentialRotated => (AuditKinds.ServiceSourceChangedV1, JsonSerializer.Serialize(change)),
            GroupChanged change when IsValid(change) => (AuditKinds.GroupChangedV1, JsonSerializer.Serialize(change)),
            PublicationChanged change when IsValid(change) => (AuditKinds.PublicationChangedV1, JsonSerializer.Serialize(change)),
            PublicationTemplateChanged change when IsValid(change) => (AuditKinds.PublicationTemplateChangedV1, JsonSerializer.Serialize(change)),
            InstancePolicyChanged change when IsValid(change) => (AuditKinds.InstancePolicyChangedV1, JsonSerializer.Serialize(change)),
            ServiceInstanceChanged change when IsValid(change) => (AuditKinds.ServiceInstanceChangedV1, JsonSerializer.Serialize(change)),
            DeploymentLinkChanged change when IsValid(change) => (AuditKinds.DeploymentLinkChangedV1, JsonSerializer.Serialize(change)),
            ServiceCollectionChanged change when IsValid(change) => (AuditKinds.ServiceCollectionChangedV1, JsonSerializer.Serialize(change)),
            LogQuotaChanged change when IsValid(change) => (AuditKinds.LogQuotaChangedV1, JsonSerializer.Serialize(change)),
            TraceQuotaChanged change when IsValid(change) => (AuditKinds.TraceQuotaChangedV1, JsonSerializer.Serialize(change)),
            CollectorChanged change when IsValid(change) => (AuditKinds.CollectorChangedV1, JsonSerializer.Serialize(change)),
            HealthTargetChanged change when IsValid(change) => (AuditKinds.HealthTargetChangedV1, JsonSerializer.Serialize(change)),
            HealthNetworkPolicyChanged change when IsValid(change) => (AuditKinds.HealthNetworkPolicyChangedV1, JsonSerializer.Serialize(change)),
            _ => throw new ArgumentException("Unsupported or invalid audit event details.", nameof(audit))
        };
        if (details.Length > 2048) throw new ArgumentException("Audit event details exceed the supported limit.", nameof(audit));
        return (kind, details);
    }

    public static bool IsValid(EventEvidenceAccessed access) => access.EvidenceId.Version == 7 && (access.Kind switch
    {
        "host-log" => access.DeviceId is { Version: 7 } && access.AgentId is { Version: 7 },
        "host-log-list" => access.DeviceId == access.EvidenceId && access.AgentId is null,
        "sigma-rule" => access.DeviceId is null && access.AgentId is null,
        "yara-scan" => access.DeviceId is { Version: 7 } && access.AgentId is null,
        "yara-list" => access.DeviceId == access.EvidenceId && access.AgentId is null,
        _ => false
    });

    public static bool IsValid(EventTimelineAccessed access) => access.From.Offset == TimeSpan.Zero && access.Until.Offset == TimeSpan.Zero &&
        access.Until > access.From && access.Until - access.From <= TimeSpan.FromDays(31) && access.GroupId is null or { Version: 7 } &&
        (access.Source is null || access.Source.Length <= 32) && access.Page is >= 1 and <= 20 && access.ItemCount is >= 0 and <= 50;

    public static bool IsValid(BaselineExceptionRecorded change) => change.SourceId.Version == 7 && change.RevisionId.Version == 7 &&
        change.CheckId is { Length: > 0 and <= 128 } && change.CheckId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        !string.IsNullOrWhiteSpace(change.Reason) && change.Reason.Length <= 200 &&
        !change.Reason.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    public static bool IsValid(SpeedSettingsChanged change) =>
        change.Previous is not null && change.Current is not null &&
        SpeedSettingsLimits.IsValid(change.Current.IntervalHours, change.Current.MaxMiB, change.Current.TimeoutSeconds) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(AgentPackageSourceChanged change) =>
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion) &&
        (change.AddressChanged || change.CredentialChanged);

    public static bool IsValid(AgentPackageCatalogueRefreshed change) => change.PackageCount is >= 0 and <= 2000 && IsVersion(change.SourceVersion);
    public static bool IsValid(AgentPackageTrustInitialized change) => change.RootVersion > 0 && change.Sha256 is { Length: 64 } &&
        change.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsValid(AgentRegistrationRecorded change) =>
        change.Action is >= AgentRegistrationAction.EnrollmentIssued and <= AgentRegistrationAction.AgentRevoked &&
        (change.AgentId is null || change.AgentId.Value.Version == 7) && (!change.AgentActor || change.AgentId is not null);

    public static bool IsValid(AgentVersionAssigned change) => change.AgentId.Version == 7 &&
        (change.PreviousPackageId is null || change.PreviousPackageId.Value.Version == 7) &&
        (change.PackageId is null || change.PackageId.Value.Version == 7);

    public static bool IsValid(IntelligenceRevisionActivated change) => change.RevisionId.Version == 7 &&
        (change.PreviousRevisionId is null || change.PreviousRevisionId.Value.Version == 7) && change.PreviousRevisionId != change.RevisionId;

    public static bool IsValid(AdvisoryImportAbandoned change) => change.ImportId.Version == 7;

    public static bool IsValid(IntelligencePolicyChanged change) => change.Previous is not null && change.Current is not null &&
        ValidIntelligencePolicy(change.Previous) && ValidIntelligencePolicy(change.Current) && IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    private static bool ValidIntelligencePolicy(IntelligencePolicyValues value) => IntelligencePolicyLimits.Valid(
        value.UpdateIntervalSeconds, value.StaleAfterSeconds, value.MaximumBytes, value.MaximumRecords);

    public static bool IsValid(SpeedRetentionChanged change) =>
        SpeedRetentionLimits.IsValid(change.PreviousDays) && SpeedRetentionLimits.IsValid(change.CurrentDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(AuditRetentionChanged change) =>
        AuditRetentionLimits.IsValid(change.PreviousDays) && AuditRetentionLimits.IsValid(change.CurrentDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(HostMetricRetentionChanged change) =>
        HostMetricRetentionLimits.IsValid(change.PreviousRawDays, change.PreviousRollupDays) &&
        HostMetricRetentionLimits.IsValid(change.CurrentRawDays, change.CurrentRollupDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(TelemetryRetentionChanged change) =>
        TelemetryRetentionLimits.IsValid(change.PreviousLogDays, change.PreviousTraceDays) &&
        TelemetryRetentionLimits.IsValid(change.CurrentLogDays, change.CurrentTraceDays) &&
        (change.PreviousMetricRawDays is null && change.PreviousMetricRollupDays is null && change.CurrentMetricRawDays is null && change.CurrentMetricRollupDays is null ||
         change.PreviousMetricRawDays is int previousRaw && change.PreviousMetricRollupDays is int previousRollup &&
         change.CurrentMetricRawDays is int currentRaw && change.CurrentMetricRollupDays is int currentRollup &&
         TelemetryRetentionLimits.IsValidMetrics(previousRaw, previousRollup) && TelemetryRetentionLimits.IsValidMetrics(currentRaw, currentRollup)) &&
        (change.PreviousHealthDays is null && change.CurrentHealthDays is null ||
         change.PreviousHealthDays is int previousHealth && change.CurrentHealthDays is int currentHealth &&
         TelemetryRetentionLimits.IsValidHealth(previousHealth) && TelemetryRetentionLimits.IsValidHealth(currentHealth)) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(GroupChanged change) => change.Action is >= GroupAction.Created and <= GroupAction.ServicesChanged &&
        change.Added is >= 0 and <= 100 && change.Removed is >= 0 and <= 100 && change.Added + change.Removed <= 100 &&
        (change.Action is GroupAction.DevicesChanged or GroupAction.ServicesChanged || (change.Added == 0 && change.Removed == 0));

    public static bool IsValid(PublicationChanged change) => change.ResourceKind is >= PublicationResourceKind.Device and <= PublicationResourceKind.Group &&
        (change.PreviousPublic != change.CurrentPublic || change.NameChanged || change.TemplateChanged && change.ResourceKind == PublicationResourceKind.Device ||
         change.PolicyChanged && change.ResourceKind == PublicationResourceKind.Service);
    public static bool IsValid(PublicationTemplateChanged change) => change.Action is >= PublicationTemplateAction.Created and <= PublicationTemplateAction.DefaultCleared &&
        change.AffectedDevices is >= 0 and <= 10000;

    public static bool IsValid(InstancePolicyChanged change) => change.Previous is not null && change.Current is not null &&
        change.Previous.PerService > 0 && change.Previous.Total > 0 && change.Current.PerService > 0 && change.Current.Total > 0 &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);

    public static bool IsValid(ServiceInstanceChanged change) => change.ServiceId.Version == 7 &&
        change.Action is ServiceInstanceAction.Retired or ServiceInstanceAction.Restored;
    public static bool IsValid(DeploymentLinkChanged change) => change.DeviceId.Version == 7 && change.ServiceInstanceId.Version == 7 &&
        change.Action is DeploymentLinkAction.Created or DeploymentLinkAction.Ended;
    public static bool IsValid(ServiceCollectionChanged change) => change.Action is ServiceCollectionAction.Stopped or ServiceCollectionAction.Resumed or ServiceCollectionAction.Archived or ServiceCollectionAction.Restored;
    public static bool IsValid(HealthTargetChanged change) => change.ServiceId.Version == 7 && ValidHealthTarget(change.Current) &&
        IsVersion(change.CurrentVersion) && (change.Action == HealthTargetAction.Created
            ? change.Previous is null && change.PreviousVersion == ""
            : change.Action == HealthTargetAction.Updated && ValidHealthTarget(change.Previous) && IsVersion(change.PreviousVersion));
    private static bool ValidHealthTarget(HealthTargetAuditValues? value) => value is not null &&
        value.IntervalSeconds is >= 5 and <= 3600 && value.TimeoutSeconds is >= 1 and <= 60 && value.TimeoutSeconds <= value.IntervalSeconds &&
        value.FailureThreshold is >= 1 and <= 10 && value.RecoveryThreshold is >= 1 and <= 10;
    public static bool IsValid(HealthNetworkPolicyChanged change) => change.PreviousGeneration >= 0 &&
        change.PreviousGeneration < long.MaxValue && change.CurrentGeneration == change.PreviousGeneration + 1 &&
        (change.PreviousGeneration == 0 ? change.PreviousVersion == "" : IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    public static bool IsValid(CollectorChanged change) => change.MonitoringServiceId.Version == 7 &&
        (change.Action == CollectorAction.Registered ? change.SourceId is null :
            change.Action is CollectorAction.RouteAdded or CollectorAction.RouteRemoved && change.SourceId?.Version == 7);
    public static bool IsValid(LogQuotaChanged change) => ValidQuota(change.Previous) && ValidQuota(change.Current) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    public static bool IsValid(TraceQuotaChanged change) => ValidQuota(change.Previous) && ValidQuota(change.Current) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    private static bool ValidQuota(LogQuotaAuditValues? value) => value is not null && value.SourceRate > 0 && value.SourceBurst > 0 && value.GlobalRate > 0 && value.GlobalBurst > 0;

    public static bool IsValid(PingSettingsChanged change) => change.Previous is not null && change.Current is not null &&
        PingSettingsLimits.IsValid(change.Current.IntervalSeconds, change.Current.TimeoutSeconds, change.Current.FailureThreshold,
            change.Current.SuccessThreshold, change.Current.MaxConcurrency) &&
        ValidPingRetention(change.Previous) && ValidPingRetention(change.Current) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);

    private static bool ValidPingRetention(PingSettingsAuditValues value) =>
        value.RawRetentionDays is null && value.RollupRetentionDays is null ||
        value.RawRetentionDays is int raw && value.RollupRetentionDays is int rollup && PingRetentionLimits.IsValid(raw, rollup);

    public static bool IsValid(AgentReplayPolicyChanged change) =>
        change.ActivatedAt.Offset == TimeSpan.Zero && change.GraceUntil.Offset == TimeSpan.Zero &&
        change.GraceUntil - change.ActivatedAt == TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5) &&
        ((change.Previous == AgentReplayPhase.Disabled && change.Current == AgentReplayPhase.Grace && change.EnforcedAt is null) ||
         (change.Previous == AgentReplayPhase.Grace && change.Current == AgentReplayPhase.Enforced &&
          change.EnforcedAt is { Offset: var offset } enforced && offset == TimeSpan.Zero && enforced >= change.GraceUntil)) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(AgentInboxSettingsChanged change) =>
        AgentInboxPolicyLimits.IsValid(change.PreviousReplaySeconds, change.PreviousRetentionDays) &&
        AgentInboxPolicyLimits.IsValid(change.CurrentReplaySeconds, change.CurrentRetentionDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    public static bool IsValid(AgentInboxCleanupActivated change) => change.CutoverAt.Offset == TimeSpan.Zero &&
        change.LegacyRetainUntil.Offset == TimeSpan.Zero && change.LegacyRetainUntil >= change.CutoverAt &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    private static bool IsVersion(string? value)
    {
        if (value is null || value.Length != 12) return false;
        Span<byte> bytes = stackalloc byte[8];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == bytes.Length;
    }

}
