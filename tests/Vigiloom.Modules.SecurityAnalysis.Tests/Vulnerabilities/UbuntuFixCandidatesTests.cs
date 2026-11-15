using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuFixCandidatesTests
{
    private static readonly UbuntuOsvEvidence Inventory = new("Ubuntu:24.04:LTS", "openssl", "1.0-1", PackageOrigin.AssumedDistribution, true);
    private static UbuntuOsvAdvisory Advisory(string introduced, string fixedVersion) => new("Ubuntu:24.04:LTS", "openssl", false, true,
        [new("ECOSYSTEM", [new(OsvEventKind.Introduced, introduced), new(OsvEventKind.Fixed, fixedVersion)])], []);

    [TestMethod]
    public void Find_RechecksCandidateAcrossOverlappingAffectedRanges()
    {
        var first = Advisory("0", "2.0-1");
        var second = Advisory("1.0-1", "3.0-1");
        var result = UbuntuFixCandidates.Find(Inventory, [first, second], CancellationToken.None);
        Assert.AreEqual(UbuntuFixStatus.Available, result.Status);
        CollectionAssert.AreEqual(new[] { "3.0-1" }, result.Versions.ToArray());
    }

    [TestMethod]
    public void Find_UsesDebianOrderingAndDoesNotMixDifferentPackages()
    {
        var first = Advisory("0", "1:2.0~rc1-1");
        var other = Advisory("0", "3:1.0-1") with { SourcePackage = "other" };
        CollectionAssert.AreEqual(new[] { "1:2.0~rc1-1" }, UbuntuFixCandidates.Find(Inventory, [first, other], CancellationToken.None).Versions.ToArray());
        Assert.AreEqual(UbuntuFixStatus.NotAffected, UbuntuFixCandidates.Find(Inventory with { SourceVersion = "1:2.0~rc1-1" }, [first], CancellationToken.None).Status);
    }

    [TestMethod]
    public void Find_RejectsUnknownOrStillListedCandidates()
    {
        var advisory = Advisory("0", "2.0-1");
        Assert.AreEqual(UbuntuFixStatus.EvidenceUnavailable, UbuntuFixCandidates.Find(Inventory, [advisory with { Versions = ["2.0-1"] }], CancellationToken.None).Status);
        Assert.AreEqual(UbuntuFixStatus.EvidenceUnavailable, UbuntuFixCandidates.Find(Inventory, [advisory with { Withdrawn = true }], CancellationToken.None).Status);
        Assert.AreEqual(UbuntuFixStatus.EvidenceUnavailable, UbuntuFixCandidates.Find(Inventory with { CompleteAndCurrent = false }, [advisory], CancellationToken.None).Status);
        Assert.AreEqual(UbuntuFixStatus.EvidenceUnavailable, UbuntuFixCandidates.Find(Inventory, [advisory, advisory with { Ranges = [new("GIT", [])] }], CancellationToken.None).Status);
    }

    [TestMethod]
    public void Find_DeduplicatesEquivalentCandidatesBeforeApplyingLimit()
    {
        var first = Advisory("0", "2.0-1");
        var records = Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? first : Advisory("0", "0:2.0-1")).ToArray();
        var result = UbuntuFixCandidates.Find(Inventory, records, CancellationToken.None);
        Assert.HasCount(1, result.Versions);
        Assert.IsTrue(DebianVersion.TryCompare("2.0-1", result.Versions[0], out var order) && order == 0);
    }

    [TestMethod]
    public void Find_ReportsUnconfirmedFixAndWorkLimitsSeparately()
    {
        var lastAffected = Advisory("0", "2.0-1") with
        {
            Ranges = [new("ECOSYSTEM", [new(OsvEventKind.Introduced, "0"), new(OsvEventKind.LastAffected, "2.0-1")])]
        };
        Assert.AreEqual(UbuntuFixStatus.NoConfirmedFix, UbuntuFixCandidates.Find(Inventory, [lastAffected], CancellationToken.None).Status);
        var many = Enumerable.Range(1, 257).Select(i => Advisory("0", $"{i + 2}.0-1")).ToArray();
        Assert.AreEqual(UbuntuFixStatus.LimitExceeded, UbuntuFixCandidates.Find(Inventory, many, CancellationToken.None).Status);
    }

    [TestMethod]
    public void Find_BudgetIncludesInitialAndCandidateEvaluation()
    {
        var versions = Enumerable.Repeat("0.1-1", 100_000).ToArray();
        var advisory = Advisory("0", "2.0-1") with { Versions = versions };
        var repeated = Enumerable.Repeat(advisory, 51).ToArray();
        Assert.AreEqual(UbuntuFixStatus.LimitExceeded, UbuntuFixCandidates.Find(Inventory, repeated, CancellationToken.None).Status);
    }
}
