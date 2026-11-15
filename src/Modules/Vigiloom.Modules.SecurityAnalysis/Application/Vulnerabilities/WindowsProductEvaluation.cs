using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum WindowsEvaluationStatus { Ready, InventoryUnavailable, Unsupported, SourceUnavailable, Incomplete, Changed }
internal sealed record WindowsProductCandidate(string DocumentId, string Cve, bool? ProductReference,
    IReadOnlyList<MsrcRemediation> Remediations, bool HasCustomerActionNotice, bool VendorKnownAffected = false, bool OtherProductStatus = false,
    IReadOnlyList<string>? Severities = null);
internal sealed record WindowsProductAssessment(WindowsEvaluationStatus Status, Guid? HostSnapshotId = null, string? Product = null,
    IReadOnlyList<WindowsProductCandidate>? Candidates = null, bool CoverageComplete = false, MsrcCandidateResult? Evidence = null,
    WindowsPatchEvidence? QuickFix = null, WindowsPatchEvidence? Updates = null, IReadOnlyList<WindowsCveEvidence>? Cves = null,
    string? Architecture = null, Guid? RunId = null, IReadOnlyList<WindowsCatalogEvidence>? CatalogEvidence = null,
    bool? CatalogComplete = null, DateTimeOffset? CatalogCheckedAt = null);

/// <summary>Discovers MSRC product candidates for one current host; candidates alone do not establish vulnerability.</summary>
internal sealed class WindowsProductEvaluation(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock, MicrosoftCatalogCache? catalog = null, CatalogRunBudget? budget = null)
{
    private const int MaximumPages = 500;
    private const int MaximumCandidates = 100_000;
    private const int MaximumRemediations = 200_000;
    private const long MaximumCharacters = 16_000_000;

    internal async Task<WindowsProductAssessment> EvaluateAsync(InventoryOwner owner, TimeSpan maximumAge, CancellationToken token)
    {
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        await using var db = await contexts.CreateDbContextAsync(token);
        var host = await WindowsHostInventory.LoadAsync(db, owner, clock.GetUtcNow(), maximumAge, token);
        if (host is null) return new(WindowsEvaluationStatus.InventoryUnavailable);
        var product = MsrcWindowsProduct.ProductName(host.Host);
        if (product is null) return new(WindowsEvaluationStatus.Unsupported);
        var now = clock.GetUtcNow();
        var quickFix = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsQuickFix, now, maximumAge, token);
        var updates = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsUpdate, now, maximumAge, token);
        var selected = await (from source in db.IntelligenceSources.AsNoTracking()
            join activation in db.IntelligenceActivations.AsNoTracking() on source.Id equals activation.SourceId
            where source.Key == MsrcSnapshotStage.SourceKey select new { source.Id, activation.RevisionId }).SingleOrDefaultAsync(token);
        if (selected is null) return new(WindowsEvaluationStatus.SourceUnavailable);
        var lookup = new MsrcProductLookup(contexts, clock);
        var candidates = new List<WindowsProductCandidate>();
        var remediationCount = 0;
        long characters = 0;
        MsrcProductPage? previous = null;
        var complete = true;
        for (var page = 0; page < MaximumPages; page++)
        {
            var result = await lookup.FindAsync(selected.Id, selected.RevisionId, product, previous?.NextCve, token, previous);
            if (result.Status == MsrcCandidateStatus.Changed) return new(WindowsEvaluationStatus.Changed);
            if (result.Status == MsrcCandidateStatus.TooLarge) return new(WindowsEvaluationStatus.Incomplete);
            if (result.Status != MsrcCandidateStatus.Ready) return new(WindowsEvaluationStatus.SourceUnavailable);
            if (result.Items.Count > MaximumCandidates - candidates.Count) return new(WindowsEvaluationStatus.Incomplete);
            foreach (var item in result.Items)
            {
                var remediations = item.Advisory.Remediations.Where(x => item.Document.Products.TryGetValue(x.ProductId, out var name) && name == product).ToArray();
                remediationCount += remediations.Length;
                characters += item.DocumentId.Length + item.Advisory.Cve.Length;
                foreach (var remediation in remediations)
                    characters += remediation.ProductId.Length + (remediation.Description?.Length ?? 0) + (remediation.FixedBuild?.Length ?? 0) +
                        (remediation.Supersedence?.Length ?? 0) + (remediation.Subtype?.Length ?? 0);
                if (remediationCount > MaximumRemediations || characters > MaximumCharacters) return new(WindowsEvaluationStatus.Incomplete);
                var productReference = MsrcWindowsProduct.HasProductReference(item, product);
                var knownAffected = MsrcWindowsProduct.VendorKnownAffected(item, product);
                var severities = MsrcWindowsProduct.Severities(item, product);
                candidates.Add(new(item.DocumentId, item.Advisory.Cve, productReference, remediations,
                    item.Advisory.HasCustomerActionNotice, knownAffected, MsrcWindowsProduct.HasOtherProductStatus(item, product), severities));
            }
            complete &= result.CoverageComplete;
            previous = result with { Items = [], Evidence = result.Evidence is null ? null : result.Evidence with { Items = [] } };
            if (!result.HasMore)
            {
                var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, host.SnapshotId, product, candidates, complete, previous.Evidence, quickFix, updates,
                    Architecture: host.Host.Architecture, RunId: ResourceIds.Create());
                if (catalog is not null)
                {
                    var batch = await catalog.GetBatchAsync(product, WindowsPatchCorrelation.InstalledKbs(assessment).Keys.ToArray(), token, budget);
                    // The oldest observation determines when the persisted assessment needs refreshing.
                    var checkedAt = batch.Evidence.Count == 0 ? clock.GetUtcNow() : batch.Evidence.Min(x => x.ObservedAt);
                    assessment = assessment with { CatalogEvidence = batch.Evidence, CatalogComplete = batch.Complete, CatalogCheckedAt = checkedAt };
                }
                var currentHost = await WindowsHostInventory.LoadAsync(db, owner, clock.GetUtcNow(), maximumAge, token);
                var currentQuickFix = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsQuickFix, clock.GetUtcNow(), maximumAge, token);
                var currentUpdates = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsUpdate, clock.GetUtcNow(), maximumAge, token);
                if (currentHost?.SnapshotId != host.SnapshotId || result.Evidence is null ||
                    currentQuickFix?.SnapshotId != quickFix?.SnapshotId || currentUpdates?.SnapshotId != updates?.SnapshotId ||
                    !await new MsrcCandidateLookup(contexts, clock).IsCurrentAsync(result.Evidence, token)) return new(WindowsEvaluationStatus.Changed);
                var cves = WindowsPatchCorrelation.Correlate(assessment, assessment.CatalogEvidence, assessment.CatalogEvidence is null ? null : clock.GetUtcNow(), token);
                if (cves.Any(x => x.ObservedKbs.Count > 500 || x.SupersedingKbs is { Count: > 500 } || x.CatalogReferences is { Count: > 40 } ||
                    System.Text.Json.JsonSerializer.Serialize(x).Length > 8192)) return new(WindowsEvaluationStatus.Incomplete);
                return assessment with { Cves = cves };
            }
        }
        return new(WindowsEvaluationStatus.Incomplete);
    }
}
