using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Reassesses supported Windows hosts when inventory or selected MSRC evidence changes.</summary>
public sealed class WindowsReassessment(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerResolver resolver,
    IAgentOwnerFence owners, TimeProvider clock, ILogger<WindowsReassessment> logger, MicrosoftCatalogCache? catalog = null)
{
    private const int PageSize = 100;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(14);

    public async Task RunAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == MsrcSnapshotStage.SourceKey && x.Enabled, token);
        if (source is null) return;
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        var now = clock.GetUtcNow();
        if (active is null || source.LastObservedRevisionId != active.RevisionId || source.LastSuccessfulCheckAt is not { } checkedAt ||
            checkedAt > now || checkedAt < now.AddSeconds(-source.StaleAfterSeconds) ||
            !await MsrcRevisionEligibility.IsPublishableAsync(db, source.Id, active.RevisionId, token)) return;
        var start = catalog?.NextHostCursor ?? 0;
        long cursor = start;
        var wrapped = start == 0;
        var budget = new CatalogRunBudget();
        var rotationSaved = false;
        while (true)
        {
            var hosts = await db.InventorySources.AsNoTracking().Where(x => x.Source == InventorySource.HostSystem && x.RowId > cursor &&
                    (!wrapped || start == 0 || x.RowId <= start) &&
                    x.CurrentGeneration != null && x.LastStatus == InventorySnapshotStatus.Complete && x.LastOutcome == InventoryOutcome.Complete)
                .OrderBy(x => x.RowId).Take(PageSize).Select(x => new { x.RowId, x.DeviceId, x.AgentId, x.DeviceGeneration }).ToArrayAsync(token);
            if (hosts.Length == 0)
            {
                if (!wrapped) { cursor = 0; wrapped = true; continue; }
                return;
            }
            foreach (var item in hosts)
            {
                token.ThrowIfCancellationRequested();
                cursor = item.RowId;
                try
                {
                    var owner = await resolver.ResolveAsync(item.DeviceId, token);
                    if (owner is null || owner.AgentId != item.AgentId || owner.DeviceGeneration != item.DeviceGeneration) continue;
                    now = clock.GetUtcNow();
                    var host = await WindowsHostInventory.LoadAsync(db, owner, now, MaximumAge, token);
                    if (host is null || MsrcWindowsProduct.ProductName(host.Host) is null) continue;
                    var coverage = await db.VulnerabilityCoverage.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == owner.DeviceId && x.SourceId == source.Id, token);
                    if (coverage is not null && coverage.AgentId == owner.AgentId && coverage.DeviceGeneration == owner.DeviceGeneration &&
                        coverage.RevisionId == active.RevisionId && coverage.MatcherVersion == WindowsAssessmentWriter.MatcherVersion && coverage.HostSnapshotId == host.SnapshotId)
                    {
                        var inputs = await db.VulnerabilityInventoryEvidence.AsNoTracking().Where(x => x.RunId == coverage.RunId).ToDictionaryAsync(x => x.Source, token);
                        var quickFix = await CurrentSnapshotIdAsync(owner, InventorySource.WindowsQuickFix, now, token);
                        var updates = await CurrentSnapshotIdAsync(owner, InventorySource.WindowsUpdate, now, token);
                        var lastRun = await db.VulnerabilityRuns.AsNoTracking().Where(x => x.RunId == coverage.RunId)
                            .Select(x => new { x.CatalogComplete, x.CatalogCheckedAt }).SingleOrDefaultAsync(token);
                        var catalogFresh = catalog is null || lastRun?.CatalogCheckedAt is { } catalogAt && catalogAt <= now &&
                            catalogAt >= now.Subtract(lastRun.CatalogComplete == true ? TimeSpan.FromHours(23) : TimeSpan.FromMinutes(15));
                        if (catalogFresh && inputs.TryGetValue(InventorySource.WindowsQuickFix, out var previousQuick) && inputs.TryGetValue(InventorySource.WindowsUpdate, out var previousUpdate) &&
                            previousQuick.SnapshotId == quickFix && previousUpdate.SnapshotId == updates) continue;
                    }
                    var assessment = await new WindowsProductEvaluation(contexts, clock, catalog, budget).EvaluateAsync(owner, MaximumAge, token);
                    if (!rotationSaved && catalog is not null && budget.Exhausted) { catalog.NextHostCursor = item.RowId; rotationSaved = true; }
                    if (assessment.Status == WindowsEvaluationStatus.Ready)
                        await new WindowsAssessmentWriter(contexts, owners, clock).WriteAsync(owner, assessment, MaximumAge, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is InvalidDataException or DbException or DbUpdateException)
                {
                    logger.LogWarning(error, "Windows vulnerability reassessment failed for device {DeviceId}.", item.DeviceId);
                }
            }
            if (hosts.Length < PageSize)
            {
                if (!wrapped) { cursor = 0; wrapped = true; continue; }
                return;
            }
        }
    }

    private async Task<Guid?> CurrentSnapshotIdAsync(InventoryOwner owner, InventorySource source, DateTimeOffset now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await (from state in db.InventorySources.AsNoTracking()
            join snapshot in db.InventorySnapshots.AsNoTracking() on new { state.AgentId, state.Source, Generation = state.LastAttemptGeneration }
                equals new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation }
            where state.AgentId == owner.AgentId && state.DeviceId == owner.DeviceId && state.DeviceGeneration == owner.DeviceGeneration &&
                state.Source == source && snapshot.DeviceId == owner.DeviceId && snapshot.DeviceGeneration == owner.DeviceGeneration &&
                (state.LastStatus == InventorySnapshotStatus.Complete || state.LastStatus == InventorySnapshotStatus.Partial) &&
                (state.LastOutcome == InventoryOutcome.Complete || state.LastOutcome == InventoryOutcome.Partial) &&
                snapshot.Status == state.LastStatus && snapshot.Outcome == state.LastOutcome &&
                snapshot.ObservedAt <= now.AddMinutes(5) && snapshot.ObservedAt >= now.Subtract(MaximumAge)
            select (Guid?)snapshot.SnapshotId).SingleOrDefaultAsync(token);
    }
}
