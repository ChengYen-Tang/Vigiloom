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

    public async Task<OperationAuditPage> QueryServiceApprovalsAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceApprovedV1, cancellationToken);

    public async Task<OperationAuditPage> QueryServiceSourcesAsync(OperationAuditQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, AuditKinds.ServiceSourceChangedV1, cancellationToken);

    private async Task<OperationAuditPage> QueryAsync(OperationAuditQuery query, short kind, CancellationToken cancellationToken)
    {
        if (!Valid(query)) throw new ArgumentException("Use a UTC range of at most 31 days and a valid page.", nameof(query));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.AuditEvents.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To &&
                (kind != 0 ? x.Kind == kind :
                    x.Kind != AuditKinds.PingSettingsChangedV1 && x.Kind != AuditKinds.AgentReplayPolicyChangedV1 && x.Kind != AuditKinds.ServiceApprovedV1 && x.Kind != AuditKinds.ServiceSourceChangedV1))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.ResourceId, x.Kind, x.Details }).ToArrayAsync(cancellationToken);
        var entries = rows.Take(PageSize).Select(x => new OperationAuditEntry(x.OccurredAt, x.ActorId, x.ResourceId,
            x.Kind == AuditKinds.SpeedSettingsChangedV1 ? ReadSpeedSettings(x.Details) : null,
            x.Kind == AuditKinds.PingSettingsChangedV1 ? ReadPingSettings(x.Details) : null,
            x.Kind == AuditKinds.AgentReplayPolicyChangedV1 ? ReadAgentReplayPolicy(x.Details) : null,
            x.Kind == AuditKinds.ServiceApprovedV1 ? ReadServiceApproval(x.Details) : null,
            x.Kind == AuditKinds.ServiceSourceChangedV1 ? ReadServiceSource(x.Details) : null)).ToArray();
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
            return value?.Action is ServiceSourceAction.Issued or ServiceSourceAction.Revoked ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
