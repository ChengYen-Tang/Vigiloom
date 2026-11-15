using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Evaluates an inventory once per device. Persisting results requires transactional revalidation.</summary>
internal sealed class UbuntuPackageEvaluation(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 250;
    private const int MaximumPagesPerSource = 40;
    private const int MaximumFindings = 250_000;
    private const long MaximumComparisonWork = 50_000_000;

    internal async Task<LinuxPackageAssessment> EvaluateAsync(InventoryOwner owner, TimeSpan inventoryMaximumAge, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow();
        var inventory = await LinuxInventory.LoadAsync(db, owner, now, inventoryMaximumAge, token);
        if (inventory.Status != LinuxInventoryStatus.Ready) return new(LinuxEvaluationStatus.InventoryUnavailable, inventory.Status);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
        if (source is null || !Fresh(source, now)) return new(LinuxEvaluationStatus.SourceUnavailable);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        if (active is null || source.LastObservedRevisionId != active.RevisionId || !await UbuntuRevisionEligibility.IsPublishableAsync(db, source.Id, active.RevisionId, now, token))
            return new(LinuxEvaluationStatus.SourceUnavailable);

        var findings = new List<LinuxPackageFinding>();
        long comparisonWork = 0;
        foreach (var group in inventory.Packages!.GroupBy(x => x.SourcePackageName, StringComparer.Ordinal))
        {
            var valid = new List<InventoryEntry>();
            foreach (var package in group)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(package.SourcePackageName) || package.SourcePackageName.Length > 256 ||
                    string.IsNullOrWhiteSpace(package.SourcePackageVersion) || string.IsNullOrWhiteSpace(package.Architecture))
                    findings.Add(new(package.Key, null, MatchState.Unknown, PackageOrigin.Unknown, LinuxFindingReason.MissingPackageIdentity,
                        package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture));
                else valid.Add(package);
            }
            if (valid.Count == 0) continue;
            var versions = valid.GroupBy(x => x.SourcePackageVersion!, StringComparer.Ordinal).ToArray();
            string? cursor = null;
            var finished = false;
            var foundCandidates = false;
            for (var pageNumber = 0; pageNumber < MaximumPagesPerSource; pageNumber++)
            {
                var page = await UbuntuCandidates.LoadAsync(db, source.Id, active.RevisionId, inventory.Ecosystem!, group.Key!, PageSize, cursor, token);
                foreach (var record in page.Records)
                {
                    token.ThrowIfCancellationRequested();
                    foundCandidates = true;
                    if ((long)findings.Count + valid.Count > MaximumFindings) return new(LinuxEvaluationStatus.Incomplete);
                    var perPackageWork = record.Packages.Sum(x => (long)x.Versions.Count + x.Ranges.Sum(r => (long)r.Events.Count) + 1);
                    if (perPackageWork > (MaximumComparisonWork - comparisonWork) / versions.Length) return new(LinuxEvaluationStatus.Incomplete);
                    comparisonWork += perPackageWork * versions.Length;
                    foreach (var version in versions)
                    {
                        token.ThrowIfCancellationRequested();
                        var evidence = new UbuntuOsvEvidence(inventory.Ecosystem!, group.Key!, version.Key,
                            PackageOrigin.AssumedDistribution, true);
                        var states = record.Packages.Select(x => UbuntuOsvMatch.EvaluateCancellable(evidence, x with { CompleteAndCurrent = true }, token).State).ToArray();
                        var state = Aggregate(states);
                        UbuntuFixResult? fix = null;
                        if (state == MatchState.Affected)
                        {
                            fix = UbuntuFixCandidates.Find(evidence, record.Packages.Select(x => x with { CompleteAndCurrent = true }).ToArray(), token,
                                MaximumComparisonWork - comparisonWork);
                            comparisonWork += fix.Work;
                        }
                        foreach (var package in version)
                        {
                            token.ThrowIfCancellationRequested();
                            findings.Add(new(package.Key, record.Id, state, PackageOrigin.AssumedDistribution,
                                state == MatchState.Unknown ? LinuxFindingReason.AmbiguousVendorEvidence : LinuxFindingReason.VendorRange,
                                package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture,
                                fix?.Status, fix?.Versions.FirstOrDefault(), record.UbuntuPriority));
                        }
                    }
                }
                if (page.Complete) { finished = true; break; }
                cursor = page.NextAfterAdvisoryId ?? throw new InvalidDataException("Ubuntu candidate continuation is missing.");
            }
            if (!finished) return new(LinuxEvaluationStatus.Incomplete);
            if (!foundCandidates)
                foreach (var package in valid) findings.Add(new(package.Key, null, MatchState.Unknown, PackageOrigin.AssumedDistribution,
                    LinuxFindingReason.NoAdvisoryEvidence, package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture));
            if (findings.Count > MaximumFindings) return new(LinuxEvaluationStatus.Incomplete);
        }
        now = clock.GetUtcNow();
        var currentSource = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.Id, token);
        var currentActive = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        if (currentSource is null || !Fresh(currentSource, now) || !currentSource.Version.AsSpan().SequenceEqual(source.Version) ||
            currentActive is null || currentActive.RevisionId != active.RevisionId || currentSource.LastObservedRevisionId != active.RevisionId || !currentActive.Version.AsSpan().SequenceEqual(active.Version) ||
            !await UbuntuRevisionEligibility.IsPublishableAsync(db, source.Id, active.RevisionId, now, token) ||
            !await CurrentSnapshotAsync(InventorySource.HostSystem, inventory.HostSnapshotId!.Value) ||
            !await CurrentSnapshotAsync(InventorySource.LinuxDpkg, inventory.PackageSnapshotId!.Value))
            return new(LinuxEvaluationStatus.Changed);
        return new(LinuxEvaluationStatus.Ready, LinuxInventoryStatus.Ready, active.RevisionId,
            inventory.HostSnapshotId, inventory.PackageSnapshotId, findings, source.Id, inventory.Ecosystem, source.Version, active.Version, ResourceIds.Create());

        async Task<bool> CurrentSnapshotAsync(InventorySource sourceKind, Guid expected) => await (from state in db.InventorySources.AsNoTracking()
            join snapshot in db.InventorySnapshots.AsNoTracking() on new { state.AgentId, state.Source, Generation = state.CurrentGeneration }
                equals new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation }
            where state.AgentId == owner.AgentId && state.DeviceId == owner.DeviceId && state.DeviceGeneration == owner.DeviceGeneration &&
                state.Source == sourceKind && state.LastStatus == InventorySnapshotStatus.Complete && state.LastOutcome == InventoryOutcome.Complete &&
                snapshot.SnapshotId == expected && snapshot.Status == InventorySnapshotStatus.Complete && snapshot.ObservedAt <= now.AddMinutes(5) &&
                snapshot.ObservedAt >= now.Subtract(inventoryMaximumAge)
            select snapshot.RowId).AnyAsync(token);
    }

    private static bool Fresh(Domain.Intelligence.IntelligenceSource source, DateTimeOffset now) => source.Enabled &&
        source.LastSuccessfulCheckAt is { } checkedAt && checkedAt <= now && checkedAt >= now.AddSeconds(-source.StaleAfterSeconds);

    internal static MatchState Aggregate(ReadOnlySpan<MatchState> states)
    {
        if (states.Contains(MatchState.Affected)) return MatchState.Affected;
        if (states.IsEmpty || states.Contains(MatchState.Unknown) || states.Contains(MatchState.Unsupported) || states.Contains(MatchState.NotEvaluated))
            return MatchState.Unknown;
        return states.Contains(MatchState.Fixed) ? MatchState.Fixed : MatchState.NotAffected;
    }
}
