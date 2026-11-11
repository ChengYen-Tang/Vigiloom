using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Bounded, allow-listed inventory setting history.</summary>
public sealed class InventoryAuditFeed(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    public async Task<AuditFeedPage> QueryAsync(OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query)) throw new ArgumentException("Invalid audit range.", nameof(query));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.InventorySettingAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * AuditFeedLimits.PageSize).Take(AuditFeedLimits.PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.ScanIntervalSeconds, x.EffectiveScanIntervalSeconds, x.EffectiveRevision, x.ChangeVersion })
            .ToArrayAsync(token);
        var entries = rows.Take(AuditFeedLimits.PageSize).Select(row =>
        {
            var effective = new InventorySettings(row.EffectiveScanIntervalSeconds);
            var valid = InventoryConfiguration.Valid(new(row.ScanIntervalSeconds)) && effective.IsValid() && row.ChangeVersion.Length == 8 &&
                (row.ScanIntervalSeconds is null || row.ScanIntervalSeconds == effective.ScanIntervalSeconds) &&
                (row.DeviceId is not null || (row.ScanIntervalSeconds ?? InventorySettings.Default.ScanIntervalSeconds) == effective.ScanIntervalSeconds) &&
                new InventorySettingsSnapshot(InventorySettingsSnapshot.CurrentSchema, row.EffectiveRevision, effective).IsValid();
            var details = valid ? new InventorySettingsAuditDetails(row.ScanIntervalSeconds, row.EffectiveScanIntervalSeconds,
                row.EffectiveRevision, Convert.ToBase64String(row.ChangeVersion)) : null;
            return new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
                valid ? AuditFeedAction.InventorySettingsChanged : AuditFeedAction.Unknown, AuditFeedOutcome.Recorded, InventorySettings: details);
        }).ToArray();
        var more = rows.Length > AuditFeedLimits.PageSize;
        return new(AuditFeedSource.InventorySettings, entries, query.Page, more && query.Page < 1000, more && query.Page >= 1000);
    }
}
