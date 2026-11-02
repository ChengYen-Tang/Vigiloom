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
    {
        if (!Valid(query)) throw new ArgumentException("Use a UTC range of at most 31 days and a valid page.", nameof(query));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.AuditEvents.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.ResourceId, x.Kind, x.Details }).ToArrayAsync(cancellationToken);
        var entries = rows.Take(PageSize).Select(x => new OperationAuditEntry(x.OccurredAt, x.ActorId, x.ResourceId,
            x.Kind == AuditKinds.SpeedSettingsChangedV1 ? ReadSpeedSettings(x.Details) : null)).ToArray();
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
}
