using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Reads only bounded, approved audit fields for Administrator diagnosis.</summary>
public sealed class OperationAuditQueryService(IDbContextFactory<OperationsDbContext> contexts)
{
    public const int PageSize = 50;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    public static bool Valid(OperationAuditQuery query) => query.From.Offset == TimeSpan.Zero && query.To.Offset == TimeSpan.Zero &&
        query.To > query.From && query.To - query.From <= TimeSpan.FromDays(31) && query.Page is >= 1 and <= 1000;

    public async Task<OperationAuditPage> QueryAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, 0, cancellationToken);

    public async Task<OperationAuditPage> QueryPingAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.PingSettingsChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryAgentReplayAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.AgentReplayPolicyChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryAgentInboxAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.AgentInboxSettingsChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryAgentPackagesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.AgentPackageSourceChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryServiceApprovalsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceApprovedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryServiceSourcesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceSourceChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryGroupsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.GroupChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryPublicationsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.PublicationChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryInstancePoliciesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.InstancePolicyChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryServiceInstancesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceInstanceChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryServiceCollectionsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceCollectionChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryLogQuotaAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.LogQuotaChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryTraceQuotaAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.TraceQuotaChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryCollectorsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.CollectorChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryHealthTargetsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.HealthTargetChangedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryHealthNetworkPoliciesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.HealthNetworkPolicyChangedV1, cancellationToken);

    private async Task<OperationAuditPage> QueryAsync(OperationAuditQuery query, short kind, CancellationToken cancellationToken)
    {
        if (!Valid(query)) throw new ArgumentException("Use a UTC range of at most 31 days and a valid page.", nameof(query));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.AuditEvents.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To &&
                (kind != 0 ? x.Kind == kind || (kind == AuditKinds.AgentInboxSettingsChangedV1 && x.Kind == AuditKinds.AgentInboxCleanupActivatedV1) ||
                    (kind == AuditKinds.AgentPackageSourceChangedV1 && (x.Kind == AuditKinds.AgentPackageCatalogueRefreshedV1 || x.Kind == AuditKinds.AgentPackageTrustInitializedV1)) :
                    x.Kind != AuditKinds.PingSettingsChangedV1 && x.Kind != AuditKinds.AgentReplayPolicyChangedV1 && x.Kind != AuditKinds.AgentInboxSettingsChangedV1 && x.Kind != AuditKinds.AgentInboxCleanupActivatedV1 && x.Kind != AuditKinds.AgentPackageSourceChangedV1 && x.Kind != AuditKinds.AgentPackageCatalogueRefreshedV1 && x.Kind != AuditKinds.AgentPackageTrustInitializedV1 && x.Kind != AuditKinds.ServiceApprovedV1 && x.Kind != AuditKinds.ServiceSourceChangedV1 && x.Kind != AuditKinds.GroupChangedV1 && x.Kind != AuditKinds.PublicationChangedV1 && x.Kind != AuditKinds.InstancePolicyChangedV1 && x.Kind != AuditKinds.ServiceInstanceChangedV1 && x.Kind != AuditKinds.ServiceCollectionChangedV1 && x.Kind != AuditKinds.LogQuotaChangedV1 && x.Kind != AuditKinds.TraceQuotaChangedV1 && x.Kind != AuditKinds.CollectorChangedV1 && x.Kind != AuditKinds.HealthTargetChangedV1 && x.Kind != AuditKinds.HealthNetworkPolicyChangedV1))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.ResourceId, x.Kind, x.Details }).ToArrayAsync(cancellationToken);
        var entries = rows.Take(PageSize).Select(x => new OperationAuditEntry(x.OccurredAt, x.ActorId, x.ResourceId,
            x.Kind == AuditKinds.SpeedSettingsChangedV1 ? ReadSpeedSettings(x.Details) : null,
            x.Kind == AuditKinds.PingSettingsChangedV1 ? ReadPingSettings(x.Details) : null,
            x.Kind == AuditKinds.AgentReplayPolicyChangedV1 ? ReadAgentReplayPolicy(x.Details) : null,
            x.Kind == AuditKinds.ServiceApprovedV1 ? ReadServiceApproval(x.Details) : null,
            x.Kind == AuditKinds.ServiceSourceChangedV1 ? ReadServiceSource(x.Details) : null,
            x.Kind == AuditKinds.GroupChangedV1 ? ReadGroup(x.Details) : null,
            x.Kind == AuditKinds.InstancePolicyChangedV1 ? ReadInstancePolicy(x.Details) : null,
            x.Kind == AuditKinds.ServiceInstanceChangedV1 ? ReadServiceInstance(x.Details) : null,
            x.Kind == AuditKinds.ServiceCollectionChangedV1 ? ReadServiceCollection(x.Details) : null,
            x.Kind == AuditKinds.LogQuotaChangedV1 ? ReadLogQuota(x.Details) : null,
            x.Kind == AuditKinds.TraceQuotaChangedV1 ? ReadTraceQuota(x.Details) : null,
            x.Kind == AuditKinds.CollectorChangedV1 ? ReadCollector(x.Details) : null,
            x.Kind == AuditKinds.HealthTargetChangedV1 ? ReadHealthTarget(x.Details) : null,
            x.Kind == AuditKinds.HealthNetworkPolicyChangedV1 ? ReadHealthNetworkPolicy(x.Details) : null,
            x.Kind == AuditKinds.SpeedRetentionChangedV1 ? ReadSpeedRetention(x.Details) : null,
            x.Kind == AuditKinds.AgentInboxSettingsChangedV1 ? ReadAgentInboxSettings(x.Details) : null,
            x.Kind == AuditKinds.AgentInboxCleanupActivatedV1 ? ReadAgentInboxActivation(x.Details) : null,
            x.Kind == AuditKinds.AgentPackageSourceChangedV1 ? ReadAgentPackageSource(x.Details) : null,
            x.Kind == AuditKinds.AgentPackageCatalogueRefreshedV1 ? ReadAgentPackageCatalogue(x.Details) : null,
            x.Kind == AuditKinds.AgentPackageTrustInitializedV1 ? ReadAgentPackageTrust(x.Details) : null,
            x.Kind == AuditKinds.PublicationChangedV1 ? ReadPublication(x.Details) : null)).ToArray();
        var more = rows.Length > PageSize;
        return new(entries, query.Page, more && query.Page < 1000, more && query.Page == 1000);
    }

    private static SpeedSettingsChanged? ReadSpeedSettings(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<SpeedSettingsChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static SpeedRetentionChanged? ReadSpeedRetention(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<SpeedRetentionChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentInboxSettingsChanged? ReadAgentInboxSettings(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentInboxSettingsChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentPackageSourceChanged? ReadAgentPackageSource(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentPackageSourceChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentPackageCatalogueRefreshed? ReadAgentPackageCatalogue(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentPackageCatalogueRefreshed>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentPackageTrustInitialized? ReadAgentPackageTrust(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentPackageTrustInitialized>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentInboxCleanupActivated? ReadAgentInboxActivation(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentInboxCleanupActivated>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static CollectorChanged? ReadCollector(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<CollectorChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static HealthTargetChanged? ReadHealthTarget(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<HealthTargetChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static HealthNetworkPolicyChanged? ReadHealthNetworkPolicy(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<HealthNetworkPolicyChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static PingSettingsChanged? ReadPingSettings(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<PingSettingsChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static AgentReplayPolicyChanged? ReadAgentReplayPolicy(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AgentReplayPolicyChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static ServiceApproved? ReadServiceApproval(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ServiceApproved>(details, Options);
            return value?.SourceId.Version == 7 ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static ServiceSourceChanged? ReadServiceSource(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ServiceSourceChanged>(details, Options);
            return value?.Action is ServiceSourceAction.Issued or ServiceSourceAction.Revoked or ServiceSourceAction.CredentialRotated ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static PublicationChanged? ReadPublication(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<PublicationChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static GroupChanged? ReadGroup(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<GroupChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static InstancePolicyChanged? ReadInstancePolicy(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<InstancePolicyChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static ServiceInstanceChanged? ReadServiceInstance(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ServiceInstanceChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static ServiceCollectionChanged? ReadServiceCollection(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ServiceCollectionChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static LogQuotaChanged? ReadLogQuota(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<LogQuotaChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static TraceQuotaChanged? ReadTraceQuota(string details)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TraceQuotaChanged>(details, Options);
            return value is not null && OperationAuditWriter.IsValid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
