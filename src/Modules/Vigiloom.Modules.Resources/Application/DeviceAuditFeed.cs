using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Reads safe metadata from the device module's own audit records.</summary>
public sealed class DeviceAuditFeed(IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<AuditFeedPage> QueryAsync(OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query)) throw new ArgumentException("Invalid audit range.", nameof(query));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.DeviceAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * AuditFeedLimits.PageSize).Take(AuditFeedLimits.PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.Action }).ToArrayAsync(token);
        var entries = rows.Take(AuditFeedLimits.PageSize).Select(row => new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
            row.Action switch
            {
                "created" => AuditFeedAction.DeviceCreated,
                "updated" => AuditFeedAction.DeviceUpdated,
                "archived" => AuditFeedAction.DeviceArchived,
                "restored" => AuditFeedAction.DeviceRestored,
                _ => AuditFeedAction.Unknown
            }, AuditFeedOutcome.Recorded)).ToArray();
        var more = rows.Length > AuditFeedLimits.PageSize;
        return new(AuditFeedSource.Devices, entries, query.Page, more && query.Page < 1000, more && query.Page == 1000);
    }
}
