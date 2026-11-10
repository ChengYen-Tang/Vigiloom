using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Returns only allow-listed Agent audit fields from this module.</summary>
public sealed class AgentAuditFeed(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<AuditFeedPage> QueryAsync(AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query) || source is not (AuditFeedSource.AgentIdentity or AuditFeedSource.AgentSettings or AuditFeedSource.AgentQueueCapacity or AuditFeedSource.AgentMetricSettings or AuditFeedSource.HostLogSources))
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
        else if (source == AuditFeedSource.HostLogSources)
        {
            var rows = await db.HostLogSourceAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.SourceId, x.Action, x.Generation, x.Enabled, x.Version }).ToArrayAsync(token);
            more = rows.Length > AuditFeedLimits.PageSize;
            entries = rows.Take(AuditFeedLimits.PageSize).Select(row =>
            {
                var action = row.Action switch
                {
                    HostLogSourceAction.Created => AuditFeedAction.HostLogSourceCreated,
                    HostLogSourceAction.Updated => AuditFeedAction.HostLogSourceUpdated,
                    HostLogSourceAction.Retired => AuditFeedAction.HostLogSourceRetired,
                    _ => AuditFeedAction.Unknown
                };
                var valid = action != AuditFeedAction.Unknown && row.SourceId != Guid.Empty && row.Generation != Guid.Empty && row.Version.Length == 8;
                var details = valid ? new HostLogSourceAuditDetails(row.SourceId, row.Generation, row.Enabled, Convert.ToBase64String(row.Version)) : null;
                return new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId, valid ? action : AuditFeedAction.Unknown, AuditFeedOutcome.Recorded, HostLogSource: details);
            }).ToArray();
        }
        else if (source == AuditFeedSource.AgentMetricSettings)
        {
            var rows = await db.MetricSettingAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.HostIntervalSeconds, x.ProcessIntervalSeconds, x.CpuMillisecondsPerMinute, x.WorkingSetLimitMiB, x.ProcessCollectionBudgetMs, x.IoLimitMiBPerMinute,
                    x.EffectiveHostIntervalSeconds, x.EffectiveProcessIntervalSeconds, x.EffectiveCpuMillisecondsPerMinute, x.EffectiveWorkingSetLimitMiB, x.EffectiveProcessCollectionBudgetMs, x.EffectiveIoLimitMiBPerMinute, x.EffectiveRevision, x.ChangeVersion }).ToArrayAsync(token);
            more = rows.Length > AuditFeedLimits.PageSize;
            entries = rows.Take(AuditFeedLimits.PageSize).Select(row =>
            {
                var effective = new AgentMetricSettings(row.EffectiveHostIntervalSeconds, row.EffectiveProcessIntervalSeconds, row.EffectiveCpuMillisecondsPerMinute, row.EffectiveWorkingSetLimitMiB, row.EffectiveProcessCollectionBudgetMs, row.EffectiveIoLimitMiBPerMinute);
                var valid = AgentMetricConfiguration.Valid(new(row.HostIntervalSeconds, row.ProcessIntervalSeconds, row.CpuMillisecondsPerMinute, row.WorkingSetLimitMiB, row.ProcessCollectionBudgetMs, row.IoLimitMiBPerMinute)) && effective.IsValid() &&
                    row.ChangeVersion.Length == 8 && (row.HostIntervalSeconds is null || row.HostIntervalSeconds == effective.HostIntervalSeconds) &&
                    (row.ProcessIntervalSeconds is null || row.ProcessIntervalSeconds == effective.ProcessIntervalSeconds) &&
                    (row.CpuMillisecondsPerMinute is null || row.CpuMillisecondsPerMinute == effective.CpuMillisecondsPerMinute) &&
                    (row.WorkingSetLimitMiB is null || row.WorkingSetLimitMiB == effective.WorkingSetLimitMiB) &&
                    (row.ProcessCollectionBudgetMs is null || row.ProcessCollectionBudgetMs == effective.ProcessCollectionBudgetMs) &&
                    (row.IoLimitMiBPerMinute is null || row.IoLimitMiBPerMinute == effective.IoLimitMiBPerMinute) &&
                    (row.DeviceId is not null ||
                        (row.HostIntervalSeconds ?? AgentMetricSettings.Default.HostIntervalSeconds) == effective.HostIntervalSeconds &&
                        (row.ProcessIntervalSeconds ?? AgentMetricSettings.Default.ProcessIntervalSeconds) == effective.ProcessIntervalSeconds &&
                        (row.CpuMillisecondsPerMinute ?? AgentMetricSettings.Default.CpuMillisecondsPerMinute) == effective.CpuMillisecondsPerMinute &&
                        (row.WorkingSetLimitMiB ?? AgentMetricSettings.Default.WorkingSetLimitMiB) == effective.WorkingSetLimitMiB &&
                        (row.ProcessCollectionBudgetMs ?? AgentMetricSettings.Default.ProcessCollectionBudgetMs) == effective.ProcessCollectionBudgetMs &&
                        (row.IoLimitMiBPerMinute ?? AgentMetricSettings.Default.IoLimitMiBPerMinute) == effective.IoLimitMiBPerMinute) &&
                    (new AgentMetricSettingsSnapshot(AgentMetricSettingsSnapshot.CurrentSchema, row.EffectiveRevision, effective).IsValid() ||
                        row.IoLimitMiBPerMinute is null && AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(effective, row.EffectiveRevision, row.CpuMillisecondsPerMinute is null, row.WorkingSetLimitMiB is null, row.ProcessCollectionBudgetMs is null));
                var details = valid ? new AgentMetricSettingsAuditDetails(row.HostIntervalSeconds, row.ProcessIntervalSeconds,
                    row.EffectiveHostIntervalSeconds, row.EffectiveProcessIntervalSeconds, row.EffectiveRevision, Convert.ToBase64String(row.ChangeVersion),
                    row.CpuMillisecondsPerMinute, row.EffectiveCpuMillisecondsPerMinute, row.WorkingSetLimitMiB, row.EffectiveWorkingSetLimitMiB,
                    row.ProcessCollectionBudgetMs, row.EffectiveProcessCollectionBudgetMs, row.IoLimitMiBPerMinute, row.EffectiveIoLimitMiBPerMinute) : null;
                return new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
                    valid ? AuditFeedAction.AgentMetricSettingsChanged : AuditFeedAction.Unknown, AuditFeedOutcome.Recorded, MetricSettings: details);
            }).ToArray();
        }
        else if (source == AuditFeedSource.AgentQueueCapacity)
        {
            var rows = await db.QueueCapacityAudits.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new { x.OccurredAt, x.ActorId, x.DeviceId, x.PayloadBytes, x.BatchCount,
                    x.EffectivePayloadBytes, x.EffectiveBatchCount, x.EffectiveRevision, x.ChangeVersion }).ToArrayAsync(token);
            more = rows.Length > AuditFeedLimits.PageSize;
            entries = rows.Take(AuditFeedLimits.PageSize).Select(row =>
            {
                var effective = new AgentQueueCapacity(row.EffectivePayloadBytes, row.EffectiveBatchCount);
                var valid = AgentQueueCapacityConfiguration.Valid(new(row.PayloadBytes, row.BatchCount)) && effective.IsValid() &&
                    row.ChangeVersion.Length == 8 && (row.PayloadBytes is null || row.PayloadBytes == effective.PayloadBytes) &&
                    (row.BatchCount is null || row.BatchCount == effective.BatchCount) &&
                    new AgentQueueCapacitySnapshot(AgentQueueCapacitySnapshot.CurrentSchema, row.EffectiveRevision, effective).IsValid();
                var details = valid ? new AgentQueueCapacityAuditDetails(row.PayloadBytes, row.BatchCount, row.EffectivePayloadBytes,
                    row.EffectiveBatchCount, row.EffectiveRevision, Convert.ToBase64String(row.ChangeVersion)) : null;
                return new AuditFeedEntry(row.OccurredAt, row.ActorId, row.DeviceId,
                    valid ? AuditFeedAction.AgentQueueCapacityChanged : AuditFeedAction.Unknown, AuditFeedOutcome.Recorded, QueueCapacity: details);
            }).ToArray();
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
