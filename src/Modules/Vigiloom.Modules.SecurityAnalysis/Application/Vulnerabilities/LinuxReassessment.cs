using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Reassesses Linux devices when their inventory or selected vendor revision changes.</summary>
public sealed class LinuxReassessment(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerResolver resolver,
    IAgentOwnerFence owners, TimeProvider clock, ILogger<LinuxReassessment> logger, INotificationWriter? notifications = null, IDevicePingSource? devices = null, IDetectionTimelineWriter? timeline = null)
{
    private const int PageSize = 100;
    private static readonly TimeSpan InventoryMaximumAge = TimeSpan.FromDays(14);

    public async Task RunAsync(CancellationToken token)
    {
        await RunSourceAsync(false, token);
        await RunSourceAsync(true, token);
    }

    private async Task RunSourceAsync(bool debian, CancellationToken token)
    {
        var sourceKey = debian ? DebianRevisionStage.SourceKey : UbuntuRevisionStage.SourceKey;
        var matcherVersion = debian ? LinuxAssessmentWriter.DebianMatcherVersion : LinuxAssessmentWriter.MatcherVersion;
        long cursor = 0;
        await using var initial = await contexts.CreateDbContextAsync(token);
        var source = await initial.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == sourceKey && x.Enabled, token);
        if (source is null) return;
        var active = await initial.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        var now = clock.GetUtcNow();
        if (active is null || source.LastObservedRevisionId != active.RevisionId || source.LastSuccessfulCheckAt is not { } checkedAt ||
            checkedAt > now || checkedAt < now.AddSeconds(-source.StaleAfterSeconds) ||
            !(debian ? await DebianRevisionEligibility.IsEligibleAsync(initial, source.Id, active.RevisionId, token) :
                await UbuntuRevisionEligibility.IsPublishableAsync(initial, source.Id, active.RevisionId, now, token))) return;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var candidates = await db.InventorySources.AsNoTracking().Where(x => x.Source == InventorySource.LinuxDpkg && x.RowId > cursor &&
                    x.CurrentGeneration != null && x.LastStatus == InventorySnapshotStatus.Complete && x.LastOutcome == InventoryOutcome.Complete)
                .OrderBy(x => x.RowId).Take(PageSize)
                .Select(x => new { x.RowId, x.DeviceId, x.AgentId, x.DeviceGeneration }).ToArrayAsync(token);
            if (candidates.Length == 0) return;
            var agentIds = candidates.Select(x => x.AgentId).ToArray();
            var deviceIds = candidates.Select(x => x.DeviceId).Distinct().ToArray();
            var snapshots = await (from state in db.InventorySources.AsNoTracking()
                join snapshot in db.InventorySnapshots.AsNoTracking() on new { state.AgentId, state.Source, Generation = state.CurrentGeneration }
                    equals new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation }
                where agentIds.Contains(state.AgentId) && (state.Source == InventorySource.HostSystem || state.Source == InventorySource.LinuxDpkg) &&
                    state.LastStatus == InventorySnapshotStatus.Complete && state.LastOutcome == InventoryOutcome.Complete &&
                    snapshot.Status == InventorySnapshotStatus.Complete && snapshot.Outcome == InventoryOutcome.Complete
                select new { state.AgentId, state.Source, snapshot.SnapshotId }).ToArrayAsync(token);
            var currentSnapshots = snapshots.ToDictionary(x => (x.AgentId, x.Source), x => x.SnapshotId);
            var coverage = await db.VulnerabilityCoverage.AsNoTracking().Where(x => x.SourceId == source.Id && deviceIds.Contains(x.DeviceId))
                .ToDictionaryAsync(x => x.DeviceId, token);
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                cursor = candidate.RowId;
                if (coverage.TryGetValue(candidate.DeviceId, out var known) && known.AgentId == candidate.AgentId &&
                    known.DeviceGeneration == candidate.DeviceGeneration && known.RevisionId == active.RevisionId &&
                    known.MatcherVersion == matcherVersion &&
                    currentSnapshots.TryGetValue((candidate.AgentId, InventorySource.HostSystem), out var host) && host == known.HostSnapshotId &&
                    currentSnapshots.TryGetValue((candidate.AgentId, InventorySource.LinuxDpkg), out var packages) && packages == known.PackageSnapshotId) continue;
                try
                {
                    var owner = await resolver.ResolveAsync(candidate.DeviceId, token);
                    if (owner is null || owner.AgentId != candidate.AgentId || owner.DeviceGeneration != candidate.DeviceGeneration) continue;
                    var assessment = debian
                        ? await new DebianPackageEvaluation(contexts, clock).EvaluateAsync(owner, InventoryMaximumAge, token)
                        : await new UbuntuPackageEvaluation(contexts, clock).EvaluateAsync(owner, InventoryMaximumAge, token);
                    if (assessment.Status == LinuxEvaluationStatus.Ready)
                        await new LinuxAssessmentWriter(contexts, owners, clock, notifications, devices, timeline).WriteAsync(owner, assessment, InventoryMaximumAge, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is InvalidDataException or DbException or DbUpdateException)
                {
                    logger.LogWarning(error, "Vulnerability reassessment for {Source} failed for device {DeviceId}.", sourceKey, candidate.DeviceId);
                }
            }
            if (candidates.Length < PageSize) return;
        }
    }
}
