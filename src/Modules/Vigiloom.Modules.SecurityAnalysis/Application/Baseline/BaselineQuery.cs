using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

/// <summary>Returns private observations for the active Agent and selected source revisions.</summary>
public sealed class BaselineQuery(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerResolver owners,
    InventoryConfiguration inventory, IBaselineSource sources, IAgentBaselineSyncReader synchronization, IBaselineExceptionReader exceptions, TimeProvider clock)
{
    public async Task<DeviceBaselineView?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var settings = await inventory.GetAsync(deviceId, token);
        if (settings is null) return null;
        var owner = await owners.ResolveAsync(deviceId, token);
        var notes = await exceptions.GetRecentAsync(deviceId, token);
        var entries = new List<BaselineCheckView>();
        var selections = new List<BaselineSelection>();
        await using var db = await contexts.CreateDbContextAsync(token);
        foreach (var key in await sources.GetKeysAsync(token))
        {
            var (checkId, title) = key switch
            {
                BaselineSources.LinuxShadow => (BaselineSources.LinuxShadowCheck, "/etc/shadow permissions"),
                BaselineSources.WindowsNetworkSelection => (BaselineSources.WindowsNetworkSelectionCheck, "Network selection UI"),
                _ => ($"{CustomBaselineCheck.Prefix}{key[BaselineSources.LocalPrefix.Length..]}", "自訂檢查")
            };
            var selected = await sources.GetActiveAsync(key, token);
            if (selected is { Availability: BaselineSourceAvailability.Ready, SourceId: not null, RevisionId: not null,
                    ActivationVersion: not null, Check: not null })
                selections.Add(new(selected.SourceId.Value, selected.RevisionId.Value, selected.ActivationVersion, selected.Check));
            BaselineObservation? observation = null;
            if (owner is not null && selected.SourceId is { } sourceId)
            {
                var rows = db.Set<BaselineObservation>().AsNoTracking().Where(x => x.DeviceId == deviceId &&
                    x.AgentId == owner.AgentId && x.DeviceGeneration == owner.DeviceGeneration && x.SourceId == sourceId && x.CheckId == checkId);
                if (selected is { Availability: BaselineSourceAvailability.Ready, RevisionId: not null, ActivationVersion: not null })
                    observation = await rows.Where(x => x.RevisionId == selected.RevisionId && x.ActivationVersion == selected.ActivationVersion)
                        .OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
                observation ??= await rows.OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
            }
            var now = clock.GetUtcNow();
            var interval = settings.Effective.Settings.ScanIntervalSeconds;
            var current = observation is not null && selected.Availability == BaselineSourceAvailability.Ready &&
                observation.RevisionId == selected.RevisionId && observation.ActivationVersion == selected.ActivationVersion &&
                observation.ObservedAt <= now && observation.ObservedAt >= now.AddSeconds(-2L * interval);
            BaselineCheck? observedDefinition = null;
            if (observation is not null)
            {
                observedDefinition = observation.RevisionId == selected.RevisionId ? selected.Check : null;
                observedDefinition ??= BaselineDefinitions.Find(key)?.Check;
                if (observedDefinition is null && selected.SourceId is { } historicalSourceId && BaselineEligibility.IsCustomKey(key, historicalSourceId))
                {
                    var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == observation.RevisionId && x.SourceId == historicalSourceId, token);
                    if (revision is not null) observedDefinition = BaselineEligibility.ReadCustom(revision, historicalSourceId);
                }
            }
            var evidence = observation is null ? null : new BaselineEvidence(observation.RevisionId, observation.ObservedAt,
                observation.OperatingSystem, observation.Outcome, observation.Evidence, current, observedDefinition);
            var checkNotes = selected.SourceId is { } noteSource ? notes.Where(x => x.SourceId == noteSource && x.CheckId == checkId).ToArray() : [];
            entries.Add(new(key, checkId, selected.Check?.Title ?? title, selected.Availability, selected.UpstreamVersion, evidence,
                selected.Check, selected.SourceId, selected.RevisionId, checkNotes));
        }
        var desired = BaselineSettingsSnapshot.Create(settings.Effective.Settings.ScanIntervalSeconds, selections);
        var observed = await synchronization.GetAsync(deviceId, token);
        var nowUtc = clock.GetUtcNow();
        var report = owner?.AgentId == observed?.AgentId ? observed?.Report : null;
        var reportedAt = report is null ? null : observed?.ReportedAt;
        var state = observed is null || owner?.AgentId != observed.AgentId ? BaselineSyncState.Waiting
            : observed.LastHeartbeatAt is not { } heartbeat || heartbeat > nowUtc || nowUtc - heartbeat >= TimeSpan.FromSeconds(observed.OfflineAfterSeconds) ? BaselineSyncState.Offline
            : reportedAt is not { } reported || reported > nowUtc || nowUtc - reported >= TimeSpan.FromSeconds(observed.OfflineAfterSeconds) ? BaselineSyncState.Waiting
            : report!.Failure is not null ? BaselineSyncState.Failed
            : report.AppliedRevision is null ? BaselineSyncState.Waiting
            : report.AppliedRevision == desired.Revision ? BaselineSyncState.Applied : BaselineSyncState.Pending;
        return new(deviceId, owner?.AgentId, settings.Effective.Settings.ScanIntervalSeconds, entries,
            new(state, desired.Revision, report?.AppliedRevision, report?.AppliedAt, report?.LastSyncedAt, reportedAt, report?.Failure));
    }
}
