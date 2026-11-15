using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuOsvMatchTests
{
    private static readonly UbuntuOsvEvidence Inventory = new("Ubuntu:24.04:LTS", "php8.3", "8.3.6-0ubuntu0.24.04.1", PackageOrigin.AssumedDistribution, true);
    private static readonly UbuntuOsvRangeEvidence Fixed = new("ECOSYSTEM", [new(OsvEventKind.Introduced, "0"), new(OsvEventKind.Fixed, "8.3.6-0ubuntu0.24.04.2")]);
    private static readonly UbuntuOsvAdvisory Advisory = new("Ubuntu:24.04:LTS", "php8.3", false, true, [Fixed], []);

    [TestMethod]
    public void Evaluate_StopsWhenCancelledDuringVersionComparison()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => UbuntuOsvMatch.EvaluateCancellable(Inventory,
            Advisory with { Versions = [Inventory.SourceVersion] }, cancellation.Token));
    }

    [TestMethod]
    public void Evaluate_RequiresExactUbuntuScopeAndValidInventory()
    {
        Assert.AreEqual(MatchState.Affected, UbuntuOsvMatch.Evaluate(Inventory, Advisory).State);
        Assert.AreEqual(MatchState.Fixed, UbuntuOsvMatch.Evaluate(Inventory with { SourceVersion = "8.3.6-0ubuntu0.24.04.2" }, Advisory).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory with { Ecosystem = "Ubuntu:Pro:24.04:LTS" }, Advisory).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory with { Ecosystem = "Debian:12" }, Advisory).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory with { SourcePackage = "other" }, Advisory).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory with { Origin = PackageOrigin.Conflicting }, Advisory).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory, Advisory with { Withdrawn = true }).State);
        Assert.AreEqual(PackageOrigin.AssumedDistribution, UbuntuOsvMatch.Evaluate(Inventory, Advisory).Origin);
    }

    [TestMethod]
    public void Evaluate_MergesRangesAndDoesNotInventSafetyFromUnknownOrConflictingVersions()
    {
        var fixedInventory = Inventory with { SourceVersion = "8.3.6-0ubuntu0.24.04.2" };
        var open = new UbuntuOsvRangeEvidence("ECOSYSTEM", [new(OsvEventKind.Introduced, "0")]);
        var unsupported = new UbuntuOsvRangeEvidence("GIT", []);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(fixedInventory, Advisory with { Ranges = [Fixed, open] }).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(fixedInventory, Advisory with { Ranges = [Fixed, unsupported] }).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(fixedInventory, Advisory with { Versions = [fixedInventory.SourceVersion] }).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(fixedInventory, Advisory with { Versions = ["0:" + fixedInventory.SourceVersion] }).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(fixedInventory, Advisory with { Versions = ["invalid"] }).State);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(Inventory, Advisory with { Ranges = [] }).State);
        Assert.AreEqual(MatchState.Affected, UbuntuOsvMatch.Evaluate(Inventory, Advisory with { Ranges = [Fixed, unsupported] }).State);
    }
}
