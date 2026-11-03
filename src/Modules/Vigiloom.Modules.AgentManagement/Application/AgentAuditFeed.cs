using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Returns only allow-listed Agent audit fields from this module.</summary>
public sealed class AgentAuditFeed(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<AuditFeedPage> QueryAsync(AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query) || source is not (AuditFeedSource.AgentIdentity or AuditFeedSource.AgentSettings))
            throw new ArgumentException("Invalid audit source or range.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var skip = (query.Page - 1) * AuditFeedLimits.PageSize;
        AuditFeedEntry[] entries;
        bool more;
        if (source == AuditFeedSource.AgentIdentity)
        {
            var rows = await db.Audits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.AgentId, x.Action }).ToArrayAsync(token);
            more = rows.Length > AuditFeedLimits.PageSize;
            entries = rows.Take(AuditFeedLimits.PageSize).Select(row => new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
                row.Action switch
                {
                    "EnrollmentIssued" => AuditFeedAction.EnrollmentIssued,
                    "EnrollmentRevoked" => AuditFeedAction.EnrollmentRevoked,
                    "AgentEnrolled" => AuditFeedAction.AgentEnrolled,
                    "AgentRevoked" => AuditFeedAction.AgentRevoked,
                    "RotationStarted" => AuditFeedAction.RotationStarted,
                    "RotationFinished" => AuditFeedAction.RotationFinished,
                    _ => AuditFeedAction.Unknown
                }, AuditFeedOutcome.Recorded, AgentId: row.AgentId)).ToArray();
        }
        else
        {
            var rows = await db.SettingAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.HeartbeatSeconds, x.SettingsPollSeconds, x.BatchIntervalSeconds,
                    x.EffectiveHeartbeatSeconds, x.EffectiveSettingsPollSeconds, x.EffectiveBatchIntervalSeconds, x.EffectiveRevision, x.ChangeVersion })
                .ToArrayAsync(token);
            more = rows.Length > AuditFeedLimits.PageSize;
            entries = rows.Take(AuditFeedLimits.PageSize).Select(row =>
            {
                var effective = new AgentSettings(row.EffectiveHeartbeatSeconds, row.EffectiveSettingsPollSeconds, row.EffectiveBatchIntervalSeconds);
                var overrides = new AgentSettingsOverrides(row.HeartbeatSeconds, row.SettingsPollSeconds, row.BatchIntervalSeconds);
                var valid = AgentConfiguration.Valid(overrides) && effective.IsValid() && row.ChangeVersion.Length == 8 &&
                    (row.HeartbeatSeconds is null || row.HeartbeatSeconds == effective.HeartbeatSeconds) &&
                    (row.SettingsPollSeconds is null || row.SettingsPollSeconds == effective.SettingsPollSeconds) &&
                    (row.BatchIntervalSeconds is null || row.BatchIntervalSeconds == effective.BatchIntervalSeconds) &&
                    new AgentSettingsSnapshot(AgentSettingsSnapshot.CurrentSchema, row.EffectiveRevision, effective).IsValid();
                var details = valid ? new AgentSettingsAuditDetails(row.HeartbeatSeconds, row.SettingsPollSeconds, row.BatchIntervalSeconds,
                    row.EffectiveHeartbeatSeconds, row.EffectiveSettingsPollSeconds, row.EffectiveBatchIntervalSeconds,
                    row.EffectiveRevision, Convert.ToBase64String(row.ChangeVersion)) : null;
                return new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
                    valid ? AuditFeedAction.AgentSettingsChanged : AuditFeedAction.Unknown, AuditFeedOutcome.Recorded, details);
            }).ToArray();
        }
        return new(source, entries, query.Page, more && query.Page < 1000, more && query.Page == 1000);
    }
}
