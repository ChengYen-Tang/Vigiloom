using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianMatchTests
{
    private static readonly DebianInventoryEvidence Inventory = new("bookworm", "openssl", "3.0.20-1~deb12u1", "amd64", true, PackageOrigin.AssumedDistribution);
    private static readonly DebianAdvisoryEvidence Advisory = new("bookworm", "openssl", DebianAdvisoryState.FixedVersion, "3.0.20-1~deb12u2", null, true);

    [TestMethod]
    [DataRow("3.0.20-1~deb12u1", (int)MatchState.Affected)]
    [DataRow("3.0.20-1~deb12u2", (int)MatchState.Fixed)]
    [DataRow("3.0.20-1~deb12u3", (int)MatchState.Fixed)]
    [DataRow("1:3.0.1-1", (int)MatchState.Fixed)]
    public void Evaluate_UsesDistributionVersionAndFixBoundary(string version, int expected) =>
        Assert.AreEqual((MatchState)expected, DebianMatch.Evaluate(Inventory with { SourceVersion = version }, Advisory).State);

    [TestMethod]
    public void Evaluate_RequiresCurrentMatchingEvidence()
    {
        Assert.AreEqual(MatchReason.MissingEvidence, DebianMatch.Evaluate(Inventory with { CompleteAndCurrent = false }, Advisory).Reason);
        Assert.AreEqual(MatchReason.MissingEvidence, DebianMatch.Evaluate(Inventory, Advisory with { CompleteAndCurrent = false }).Reason);
        Assert.AreEqual(MatchReason.MissingEvidence, DebianMatch.Evaluate(Inventory with { Architecture = null }, Advisory).Reason);
        Assert.AreEqual(MatchReason.OriginConflict, DebianMatch.Evaluate(Inventory with { Origin = PackageOrigin.Conflicting }, Advisory).Reason);
        Assert.AreEqual(MatchReason.DifferentScope, DebianMatch.Evaluate(Inventory with { Release = "trixie" }, Advisory).Reason);
        Assert.AreEqual(MatchReason.DifferentScope, DebianMatch.Evaluate(Inventory with { SourcePackage = "other" }, Advisory).Reason);
        Assert.AreEqual(MatchReason.InvalidVersion, DebianMatch.Evaluate(Inventory with { SourceVersion = "invalid" }, Advisory).Reason);
        Assert.AreEqual(MatchReason.InvalidVersion, DebianMatch.Evaluate(Inventory with { SourceVersion = "invalid" },
            Advisory with { Architectures = new HashSet<string> { "arm64" } }).Reason);
        Assert.AreEqual(MatchReason.InvalidArchitectureScope, DebianMatch.Evaluate(Inventory,
            Advisory with { Architectures = new HashSet<string>() }).Reason);
        Assert.AreEqual(MatchReason.InvalidVersion, DebianMatch.Evaluate(Inventory, Advisory with { FixedVersion = null }).Reason);
    }

    [TestMethod]
    public void Evaluate_UsesExplicitVendorStateAndArchitecture()
    {
        Assert.AreEqual(MatchState.Affected, DebianMatch.Evaluate(Inventory, Advisory with { State = DebianAdvisoryState.Open, FixedVersion = null }).State);
        Assert.AreEqual(MatchState.NotAffected, DebianMatch.Evaluate(Inventory, Advisory with { State = DebianAdvisoryState.NotAffected }).State);
        Assert.AreEqual(MatchState.Unknown, DebianMatch.Evaluate(Inventory, Advisory with { State = DebianAdvisoryState.Undetermined }).State);
        Assert.AreEqual(MatchState.NotAffected, DebianMatch.Evaluate(Inventory, Advisory with { Architectures = new HashSet<string> { "arm64" } }).State);
        Assert.AreEqual(MatchState.Affected, DebianMatch.Evaluate(Inventory, Advisory with { Architectures = new HashSet<string> { "amd64" } }).State);
        Assert.AreEqual(PackageOrigin.AssumedDistribution, DebianMatch.Evaluate(Inventory, Advisory).Origin);
        Assert.AreEqual(PackageOrigin.VerifiedDistribution, DebianMatch.Evaluate(Inventory with { Origin = PackageOrigin.VerifiedDistribution }, Advisory).Origin);
    }
}
