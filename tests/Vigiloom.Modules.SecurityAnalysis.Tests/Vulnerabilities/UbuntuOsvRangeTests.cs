using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuOsvRangeTests
{
    private static OsvVersionEvent Start(string value) => new(OsvEventKind.Introduced, value);
    private static OsvVersionEvent Fix(string value) => new(OsvEventKind.Fixed, value);

    [TestMethod]
    [DataRow("8.4.5-1ubuntu1", (int)MatchState.Affected)]
    [DataRow("8.4.5-1ubuntu1.1", (int)MatchState.Fixed)]
    [DataRow("8.4.5-1ubuntu1.2", (int)MatchState.Fixed)]
    public void Evaluate_UsesUbuntuVersionAndExclusiveFixBoundary(string version, int expected) =>
        Assert.AreEqual((MatchState)expected, UbuntuOsvRange.Evaluate(version, [Start("0"), Fix("8.4.5-1ubuntu1.1")]));

    [TestMethod]
    public void Evaluate_HandlesUnsortedEventsAndSeparateIntervals()
    {
        OsvVersionEvent[] events = [Fix("4.0"), Start("3.0"), Fix("2.0"), Start("1.0")];
        Assert.AreEqual(MatchState.NotAffected, UbuntuOsvRange.Evaluate("0.9", events));
        Assert.AreEqual(MatchState.Affected, UbuntuOsvRange.Evaluate("1.5", events));
        Assert.AreEqual(MatchState.Fixed, UbuntuOsvRange.Evaluate("2.0", events));
        Assert.AreEqual(MatchState.Affected, UbuntuOsvRange.Evaluate("3.5", events));
        Assert.AreEqual(MatchState.Fixed, UbuntuOsvRange.Evaluate("4.0", events));
    }

    [TestMethod]
    public void Evaluate_DoesNotTreatUntriagedOpenRangeAsConfirmedAffected()
    {
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("1.0", [Start("0")]));
        Assert.AreEqual(MatchState.Affected, UbuntuOsvRange.Evaluate("1.0", [Start("0"), Fix("2.0"), Start("3.0")]));
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("3.5", [Start("0"), Fix("2.0"), Start("3.0")]));
    }

    [TestMethod]
    public void Evaluate_RespectsInclusiveLastAffectedAndRejectsUnsupportedRanges()
    {
        OsvVersionEvent[] bounded = [Start("0"), new(OsvEventKind.LastAffected, "2.0")];
        Assert.AreEqual(MatchState.Affected, UbuntuOsvRange.Evaluate("2.0", bounded));
        Assert.AreEqual(MatchState.NotAffected, UbuntuOsvRange.Evaluate("2.1", bounded));
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("1.0", [Start("0"), new(OsvEventKind.Limit, "2.0")]));
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("1.0", [Start("0"), Fix("invalid")]));
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("1.0", [Start("1.0"), Fix("1.0")]));
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvRange.Evaluate("invalid", [Start("0"), Fix("2.0")]));
    }
}
