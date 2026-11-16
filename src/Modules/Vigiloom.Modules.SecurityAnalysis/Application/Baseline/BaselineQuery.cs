using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

/// <summary>Returns private observations for the active Agent and selected source revisions.</summary>
public sealed class BaselineQuery(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerResolver owners,
    InventoryConfiguration inventory, IBaselineSource sources, TimeProvider clock)
{
    public async Task<DeviceBaselineView?> GetAsync(Guid deviceId, CancellationToken token)
    {
        var settings = await inventory.GetAsync(deviceId, token);
        if (settings is null) return null;
        var owner = await owners.ResolveAsync(deviceId, token);
        var entries = new List<BaselineCheckView>(2);
        await using var db = await contexts.CreateDbContextAsync(token);
        foreach (var (key, checkId, title) in new[]
        {
            (BaselineSources.LinuxShadow, BaselineSources.LinuxShadowCheck, "/etc/shadow permissions"),
            (BaselineSources.WindowsNetworkSelection, BaselineSources.WindowsNetworkSelectionCheck, "Network selection UI")
        })
        {
            var selected = await sources.GetActiveAsync(key, token);
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
            var evidence = observation is null ? null : new BaselineEvidence(observation.RevisionId, observation.ObservedAt,
                observation.OperatingSystem, observation.Outcome, observation.Evidence, current);
            entries.Add(new(key, checkId, selected.Check?.Title ?? title, selected.Availability, selected.UpstreamVersion, evidence));
        }
        return new(deviceId, owner?.AgentId, settings.Effective.Settings.ScanIntervalSeconds, entries);
    }
}
