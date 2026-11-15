using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class WindowsPatchCorrelationTests(TestContext context)
{
    [TestMethod]
    public void Correlate_UsesOnlyInstalledObservationsAndKeepsActionAndRebootEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var quickFix = new WindowsPatchEvidence(InventorySource.WindowsQuickFix, Guid.CreateVersion7(), InventoryOutcome.Complete, now,
            [new("KB1234567", "patch")]);
        var updates = new WindowsPatchEvidence(InventorySource.WindowsUpdate, Guid.CreateVersion7(), InventoryOutcome.Partial, now,
            [new("a", "installed", UpdateInstalled: true, UpdateRebootRequired: true, UpdateKbs: ["2345678"]),
             new("b", "not installed", UpdateInstalled: false, UpdateKbs: ["3456789"])]);
        var candidate = new WindowsProductCandidate("2025-Nov", "CVE-2025-1234", true,
            [new("p", "1234567", null, null, null), new("p", "2345678", null, null, null), new("p", "3456789", null, null, null)], true);
        var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, Candidates: [candidate], QuickFix: quickFix, Updates: updates);

        var result = WindowsPatchCorrelation.Correlate(assessment, token: context.CancellationToken).Single();
        CollectionAssert.AreEqual(new[] { "1234567", "2345678" }, result.ObservedKbs.ToArray());
        Assert.IsTrue(result.HasCustomerActionNotice);
        Assert.IsTrue(result.RebootObserved);
        Assert.IsFalse(result.AmbiguousProduct);
    }

    [TestMethod]
    public void Classify_ReportsVendorProductRiskWithoutClaimingPatchStatus()
    {
        var evidence = new WindowsCveEvidence("CVE-2025-1234", [], false, false, false, true);
        Assert.AreEqual(MatchState.Affected, WindowsPatchCorrelation.Classify(evidence));
        Assert.AreEqual(MatchState.Fixed, WindowsPatchCorrelation.Classify(evidence with { ObservedKbs = ["1234567"] }));
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(evidence with { ObservedKbs = ["1234567"], RebootObserved = true }));
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(evidence with { ObservedKbs = ["1234567"], AmbiguousProduct = true }));
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(evidence with { AmbiguousProduct = true }));
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(evidence with { VendorKnownAffected = false }));
    }

    [TestMethod]
    public void Correlate_TreatsConflictingProductStatusesAcrossDocumentsAsUnknown()
    {
        var known = new WindowsProductCandidate("2025-Nov", "CVE-2025-1234", true, [], false, true);
        var conflicting = new WindowsProductCandidate("2025-Dec", "CVE-2025-1234", true, [], false, false, true);
        var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, Candidates: [known, conflicting]);
        var result = WindowsPatchCorrelation.Correlate(assessment, token: context.CancellationToken).Single();
        Assert.IsTrue(result.VendorKnownAffected);
        Assert.IsTrue(result.AmbiguousProduct);
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(result));
        var sameDocumentConflict = assessment with { Candidates = [known with { OtherProductStatus = true }] };
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(WindowsPatchCorrelation.Correlate(sameDocumentConflict, token: context.CancellationToken).Single()));
    }

    [TestMethod]
    public void Correlate_ReportsHighestVendorSeverityWithoutChangingMatchState()
    {
        var candidates = new[]
        {
            new WindowsProductCandidate("2025-Nov", "CVE-2025-1234", true, [], false, Severities: ["Moderate"]),
            new WindowsProductCandidate("2025-Dec", "CVE-2025-1234", true, [], false, Severities: ["Critical"])
        };
        var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, Candidates: candidates);
        var result = WindowsPatchCorrelation.Correlate(assessment, token: context.CancellationToken).Single();
        Assert.AreEqual("Critical", result.MicrosoftSeverity);
        Assert.AreEqual(MatchState.Unknown, WindowsPatchCorrelation.Classify(result));
        Assert.AreEqual("Critical", WindowsFindingDetailsReader.Read(System.Text.Json.JsonSerializer.Serialize(result))!.MicrosoftSeverity);
    }

    [TestMethod]
    public void Correlate_UsesOnlyInstalledReplacementFromSameProduct()
    {
        const string product = "Windows Server 2022";
        var now = DateTimeOffset.UtcNow;
        var quickFix = new WindowsPatchEvidence(InventorySource.WindowsQuickFix, Guid.CreateVersion7(), InventoryOutcome.Complete, now,
            [new("KB5031364", "installed update")]);
        var candidate = new WindowsProductCandidate("2023-Oct", "CVE-2023-1234", true,
            [new("p", "5006699", null, null, null)], false, true);
        var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, Product: product, Candidates: [candidate], QuickFix: quickFix);
        var relation = new WindowsCatalogEvidence(product, "5031364", "db2ed84f-9b54-41f7-b056-eb5d42725946", now, ["5006699"]);
        var result = WindowsPatchCorrelation.Correlate(assessment, [relation], now, context.CancellationToken).Single();
        CollectionAssert.AreEqual(new[] { "5031364" }, result.SupersedingKbs!.ToArray());
        var reference = result.CatalogReferences!.Single();
        Assert.AreEqual("5031364", reference.InstalledKb);
        Assert.AreEqual("5006699", reference.SupersededKb);
        Assert.AreEqual(Guid.Parse(relation.UpdateId), reference.UpdateId);
        Assert.AreEqual(now, reference.ObservedAt);
        var saved = WindowsFindingDetailsReader.Read(System.Text.Json.JsonSerializer.Serialize(result));
        Assert.IsNotNull(saved);
        Assert.AreEqual(reference, saved.CatalogReferences!.Single());
        Assert.AreEqual(MatchState.Fixed, WindowsPatchCorrelation.Classify(result));
        var wrongProduct = WindowsPatchCorrelation.Correlate(assessment, [relation with { Product = "Windows Server 2019" }], now, context.CancellationToken).Single();
        Assert.IsEmpty(wrongProduct.SupersedingKbs!);
        Assert.AreEqual(MatchState.Affected, WindowsPatchCorrelation.Classify(wrongProduct));
        var absent = WindowsPatchCorrelation.Correlate(assessment, [relation with { InstalledKb = "5031365" }], now, context.CancellationToken).Single();
        Assert.IsEmpty(absent.SupersedingKbs!);
        var stale = WindowsPatchCorrelation.Correlate(assessment, [relation with { ObservedAt = now.AddDays(-2) }], now, context.CancellationToken).Single();
        Assert.IsEmpty(stale.SupersedingKbs!);
        var future = WindowsPatchCorrelation.Correlate(assessment, [relation with { ObservedAt = now.AddSeconds(1) }], now, context.CancellationToken).Single();
        Assert.IsEmpty(future.SupersedingKbs!);
        var malformed = WindowsPatchCorrelation.Correlate(assessment, [relation with { UpdateId = "invalid" }], now, context.CancellationToken).Single();
        Assert.IsEmpty(malformed.SupersedingKbs!);
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsPatchCorrelation.Correlate(assessment,
            [relation with { SupersededKbs = Enumerable.Repeat("5006699", 50_001).ToArray() }], now, context.CancellationToken));
    }

    [TestMethod]
    public void Correlate_LimitsOptionalSourceDetailsWithoutDiscardingTheAssessment()
    {
        var now = DateTimeOffset.UtcNow;
        const string product = "Windows Server 2022";
        var patches = Enumerable.Range(0, 41).Select(x => new InventoryEntry($"{5031300 + x}", "patch")).ToArray();
        var quickFix = new WindowsPatchEvidence(InventorySource.WindowsQuickFix, Guid.CreateVersion7(), InventoryOutcome.Complete, now, patches);
        var candidate = new WindowsProductCandidate("2023-Oct", "CVE-2023-1234", true,
            [new("p", "5006699", null, null, null)], false, true);
        var assessment = new WindowsProductAssessment(WindowsEvaluationStatus.Ready, Product: product, Candidates: [candidate], QuickFix: quickFix);
        var relations = patches.Select(x => new WindowsCatalogEvidence(product, x.Key, "db2ed84f-9b54-41f7-b056-eb5d42725946", now, ["5006699"])).ToArray();
        var result = WindowsPatchCorrelation.Correlate(assessment, relations, now, context.CancellationToken).Single();
        Assert.HasCount(41, result.SupersedingKbs!);
        Assert.HasCount(40, result.CatalogReferences!);
        Assert.IsTrue(result.CatalogReferencesTruncated);
        Assert.AreEqual(MatchState.Fixed, WindowsPatchCorrelation.Classify(result));
        Assert.IsNotNull(WindowsFindingDetailsReader.Read(System.Text.Json.JsonSerializer.Serialize(result)));
    }
}
