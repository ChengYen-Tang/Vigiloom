using System.Data;
using System.Data.Common;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Writes approved structured audit events within the caller's SQL transaction.</summary>
public sealed class OperationAuditWriter : IOperationAuditWriter<OperationAudit>
{
    public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(audit);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("An open connection and its active transaction are required.");
        if (audit.ActorId == Guid.Empty || audit.ResourceId == Guid.Empty || audit.OccurredAt.Offset != TimeSpan.Zero || audit.OccurredAt == default)
            throw new ArgumentException("Invalid audit event metadata.", nameof(audit));

        var (kind, details) = audit.Details switch
        {
            SpeedSettingsChanged change when IsValid(change) => (AuditKinds.SpeedSettingsChangedV1, JsonSerializer.Serialize(change)),
            SpeedRetentionChanged change when IsValid(change) => (AuditKinds.SpeedRetentionChangedV1, JsonSerializer.Serialize(change)),
            PingSettingsChanged change when IsValid(change) => (AuditKinds.PingSettingsChangedV1, JsonSerializer.Serialize(change)),
            AgentReplayPolicyChanged change when IsValid(change) => (AuditKinds.AgentReplayPolicyChangedV1, JsonSerializer.Serialize(change)),
            AgentInboxSettingsChanged change when IsValid(change) => (AuditKinds.AgentInboxSettingsChangedV1, JsonSerializer.Serialize(change)),
            AgentInboxCleanupActivated change when IsValid(change) => (AuditKinds.AgentInboxCleanupActivatedV1, JsonSerializer.Serialize(change)),
            AgentPackageSourceChanged change when IsValid(change) => (AuditKinds.AgentPackageSourceChangedV1, JsonSerializer.Serialize(change)),
            AgentPackageCatalogueRefreshed change when IsValid(change) => (AuditKinds.AgentPackageCatalogueRefreshedV1, JsonSerializer.Serialize(change)),
            AgentPackageTrustInitialized change when IsValid(change) => (AuditKinds.AgentPackageTrustInitializedV1, JsonSerializer.Serialize(change)),
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details) VALUES (@actor, @resource, @time, @kind, @details)";
        Add(command, "@actor", DbType.Guid, audit.ActorId);
        Add(command, "@resource", DbType.Guid, audit.ResourceId);
        Add(command, "@time", DbType.DateTimeOffset, audit.OccurredAt);
        Add(command, "@kind", DbType.Int16, kind);
        Add(command, "@details", DbType.String, details, 2048);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static bool IsValid(SpeedSettingsChanged change) =>
        change.Previous is not null && change.Current is not null &&
        SpeedSettingsLimits.IsValid(change.Current.IntervalHours, change.Current.MaxMiB, change.Current.TimeoutSeconds) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(AgentPackageSourceChanged change) =>
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion) &&
        (change.AddressChanged || change.CredentialChanged);

    internal static bool IsValid(AgentPackageCatalogueRefreshed change) => change.PackageCount is >= 0 and <= 2000 && IsVersion(change.SourceVersion);
    internal static bool IsValid(AgentPackageTrustInitialized change) => change.RootVersion > 0 && change.Sha256 is { Length: 64 } &&
        change.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsValid(SpeedRetentionChanged change) =>
        SpeedRetentionLimits.IsValid(change.PreviousDays) && SpeedRetentionLimits.IsValid(change.CurrentDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(GroupChanged change) => change.Action is >= GroupAction.Created and <= GroupAction.ServicesChanged &&
        change.Added is >= 0 and <= 100 && change.Removed is >= 0 and <= 100 && change.Added + change.Removed <= 100 &&
        (change.Action is GroupAction.DevicesChanged or GroupAction.ServicesChanged || (change.Added == 0 && change.Removed == 0));

    internal static bool IsValid(PublicationChanged change) => change.ResourceKind is >= PublicationResourceKind.Device and <= PublicationResourceKind.Group &&
        (change.PreviousPublic != change.CurrentPublic || change.NameChanged || change.TemplateChanged && change.ResourceKind == PublicationResourceKind.Device ||
         change.PolicyChanged && change.ResourceKind == PublicationResourceKind.Service);
    internal static bool IsValid(PublicationTemplateChanged change) => change.Action is >= PublicationTemplateAction.Created and <= PublicationTemplateAction.DefaultCleared &&
        change.AffectedDevices is >= 0 and <= 10000;

    internal static bool IsValid(InstancePolicyChanged change) => change.Previous is not null && change.Current is not null &&
        change.Previous.PerService > 0 && change.Previous.Total > 0 && change.Current.PerService > 0 && change.Current.Total > 0 &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(ServiceInstanceChanged change) => change.ServiceId.Version == 7 &&
        change.Action is ServiceInstanceAction.Retired or ServiceInstanceAction.Restored;
    internal static bool IsValid(DeploymentLinkChanged change) => change.DeviceId.Version == 7 && change.ServiceInstanceId.Version == 7 &&
        change.Action is DeploymentLinkAction.Created or DeploymentLinkAction.Ended;
    internal static bool IsValid(ServiceCollectionChanged change) => change.Action is ServiceCollectionAction.Stopped or ServiceCollectionAction.Resumed;
    internal static bool IsValid(HealthTargetChanged change) => change.ServiceId.Version == 7 && ValidHealthTarget(change.Current) &&
        IsVersion(change.CurrentVersion) && (change.Action == HealthTargetAction.Created
            ? change.Previous is null && change.PreviousVersion == ""
            : change.Action == HealthTargetAction.Updated && ValidHealthTarget(change.Previous) && IsVersion(change.PreviousVersion));
    private static bool ValidHealthTarget(HealthTargetAuditValues? value) => value is not null &&
        value.IntervalSeconds is >= 5 and <= 3600 && value.TimeoutSeconds is >= 1 and <= 60 && value.TimeoutSeconds <= value.IntervalSeconds &&
        value.FailureThreshold is >= 1 and <= 10 && value.RecoveryThreshold is >= 1 and <= 10;
    internal static bool IsValid(HealthNetworkPolicyChanged change) => change.PreviousGeneration >= 0 &&
        change.PreviousGeneration < long.MaxValue && change.CurrentGeneration == change.PreviousGeneration + 1 &&
        (change.PreviousGeneration == 0 ? change.PreviousVersion == "" : IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    internal static bool IsValid(CollectorChanged change) => change.MonitoringServiceId.Version == 7 &&
        (change.Action == CollectorAction.Registered ? change.SourceId is null :
            change.Action is CollectorAction.RouteAdded or CollectorAction.RouteRemoved && change.SourceId?.Version == 7);
    internal static bool IsValid(LogQuotaChanged change) => ValidQuota(change.Previous) && ValidQuota(change.Current) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    internal static bool IsValid(TraceQuotaChanged change) => ValidQuota(change.Previous) && ValidQuota(change.Current) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);
    private static bool ValidQuota(LogQuotaAuditValues? value) => value is not null && value.SourceRate > 0 && value.SourceBurst > 0 && value.GlobalRate > 0 && value.GlobalBurst > 0;

    internal static bool IsValid(PingSettingsChanged change) => change.Previous is not null && change.Current is not null &&
        PingSettingsLimits.IsValid(change.Current.IntervalSeconds, change.Current.TimeoutSeconds, change.Current.FailureThreshold,
            change.Current.SuccessThreshold, change.Current.MaxConcurrency) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(AgentReplayPolicyChanged change) =>
        change.ActivatedAt.Offset == TimeSpan.Zero && change.GraceUntil.Offset == TimeSpan.Zero &&
        change.GraceUntil - change.ActivatedAt == TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5) &&
        ((change.Previous == AgentReplayPhase.Disabled && change.Current == AgentReplayPhase.Grace && change.EnforcedAt is null) ||
         (change.Previous == AgentReplayPhase.Grace && change.Current == AgentReplayPhase.Enforced &&
          change.EnforcedAt is { Offset: var offset } enforced && offset == TimeSpan.Zero && enforced >= change.GraceUntil)) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(AgentInboxSettingsChanged change) =>
        AgentInboxPolicyLimits.IsValid(change.PreviousReplaySeconds, change.PreviousRetentionDays) &&
        AgentInboxPolicyLimits.IsValid(change.CurrentReplaySeconds, change.CurrentRetentionDays) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(AgentInboxCleanupActivated change) => change.CutoverAt.Offset == TimeSpan.Zero &&
        change.LegacyRetainUntil.Offset == TimeSpan.Zero && change.LegacyRetainUntil >= change.CutoverAt &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    private static bool IsVersion(string? value)
    {
        if (value is null || value.Length != 12) return false;
        Span<byte> bytes = stackalloc byte[8];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == bytes.Length;
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}
