using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Matches Debian source packages against the selected security tracker revision.</summary>
internal sealed class DebianPackageEvaluation(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 250;
    private const int MaximumPagesPerPackage = 40;
    private const int MaximumFindings = 250_000;

    internal async Task<LinuxPackageAssessment> EvaluateAsync(InventoryOwner owner, TimeSpan maximumAge, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow();
        var inventory = await LinuxInventory.LoadAsync(db, owner, now, maximumAge, token, debian: true);
        if (inventory.Status != LinuxInventoryStatus.Ready) return new(LinuxEvaluationStatus.InventoryUnavailable, inventory.Status);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == DebianRevisionStage.SourceKey, token);
        if (source is null || !source.Enabled || source.LastSuccessfulCheckAt is not { } checkedAt ||
            checkedAt > now || checkedAt < now.AddSeconds(-source.StaleAfterSeconds)) return new(LinuxEvaluationStatus.SourceUnavailable);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        if (active is null || source.LastObservedRevisionId != active.RevisionId ||
            !await DebianRevisionEligibility.IsEligibleAsync(db, source.Id, active.RevisionId, token))
            return new(LinuxEvaluationStatus.SourceUnavailable);

        var findings = new List<LinuxPackageFinding>();
        foreach (var group in inventory.Packages!.GroupBy(x => x.SourcePackageName, StringComparer.Ordinal))
        {
            var packages = new List<InventoryEntry>();
            foreach (var package in group)
            {
                if (string.IsNullOrWhiteSpace(package.SourcePackageName) || package.SourcePackageName.Length > 256 ||
                    string.IsNullOrWhiteSpace(package.SourcePackageVersion) || string.IsNullOrWhiteSpace(package.Architecture))
                    findings.Add(new(package.Key, null, MatchState.Unknown, PackageOrigin.Unknown, LinuxFindingReason.MissingPackageIdentity,
                        package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture));
                else packages.Add(package);
            }
            if (packages.Count == 0) continue;
            long cursor = 0;
            var found = false;
            var complete = false;
            for (var page = 0; page < MaximumPagesPerPackage; page++)
            {
                var advisories = await db.DebianAdvisories.AsNoTracking().Where(x => x.RevisionId == active.RevisionId &&
                    x.Release == inventory.Ecosystem && x.SourcePackage == group.Key && x.RowId > cursor)
                    .OrderBy(x => x.RowId).Take(PageSize).ToArrayAsync(token);
                if ((long)findings.Count + (long)packages.Count * advisories.Length > MaximumFindings)
                    return new(LinuxEvaluationStatus.Incomplete);
                foreach (var advisory in advisories)
                {
                    found = true;
                    foreach (var package in packages)
                    {
                        var match = DebianMatch.Evaluate(new(inventory.Ecosystem, package.SourcePackageName, package.SourcePackageVersion,
                            package.Architecture, true, PackageOrigin.AssumedDistribution),
                            new(advisory.Release, advisory.SourcePackage, (DebianAdvisoryState)advisory.State, advisory.FixedVersion, null, true));
                        var fix = match.State == MatchState.Affected
                            ? advisory.FixedVersion is not null ? UbuntuFixStatus.Available : UbuntuFixStatus.NoConfirmedFix
                            : (UbuntuFixStatus?)null;
                        findings.Add(new(package.Key, advisory.AdvisoryId, match.State, match.Origin,
                            match.State == MatchState.Unknown ? LinuxFindingReason.AmbiguousVendorEvidence : LinuxFindingReason.VendorRange,
                            package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture, fix, advisory.FixedVersion));
                    }
                }
                if (advisories.Length < PageSize) { complete = true; break; }
                cursor = advisories[^1].RowId;
            }
            if (!complete) return new(LinuxEvaluationStatus.Incomplete);
            if (!found)
                foreach (var package in packages) findings.Add(new(package.Key, null, MatchState.Unknown, PackageOrigin.AssumedDistribution,
                    LinuxFindingReason.NoAdvisoryEvidence, package.SourcePackageName, package.SourcePackageVersion, package.Version, package.Architecture));
            if (findings.Count > MaximumFindings) return new(LinuxEvaluationStatus.Incomplete);
        }
        return new(LinuxEvaluationStatus.Ready, LinuxInventoryStatus.Ready, active.RevisionId, inventory.HostSnapshotId,
            inventory.PackageSnapshotId, findings, source.Id, inventory.Ecosystem, source.Version, active.Version, ResourceIds.Create(), IsDebian: true);
    }
}
