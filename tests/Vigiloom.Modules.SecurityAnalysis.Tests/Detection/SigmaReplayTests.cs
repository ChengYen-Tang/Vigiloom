using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaReplayTests
{
    private const string Rule = """
        title: Login failure
        logsource:
          product: windows
          service: security
        detection:
          selection:
            EventID: 4625
            TargetUserName: alice
          condition: selection
        """;

    [TestMethod]
    public void Run_MatchAndNonMatch_PassesAndRetainsAttribution()
    {
        var snapshot = Snapshot();
        var rule = Entry();
        var positive = Event("4625", "alice");
        var negative = Event("4625", "bob");

        var result = SigmaReplay.Run(new(snapshot, [rule], []), rule.Source.Path, [new(positive, SigmaMatch.Yes), new(negative, SigmaMatch.No)]);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(snapshot.RevisionId, result.Snapshot.RevisionId);
        Assert.AreEqual(rule.Source.Sha256, result.Source.Sha256);
        Assert.AreEqual(positive.MessageId, result.Observations[0].MessageId);
        Assert.AreEqual(SigmaMatch.Yes, result.Observations[0].Actual);
        Assert.AreEqual(SigmaMatch.No, result.Observations[1].Actual);
    }

    [TestMethod]
    public void Run_MissingField_DoesNotPassAsNegative()
    {
        var result = SigmaReplay.Run(RuleSet(), "rules/login.yml", [new(Event("4625", "alice"), SigmaMatch.Yes), new(Event("4625", null), SigmaMatch.No)]);

        Assert.IsFalse(result.Passed);
        Assert.AreEqual(SigmaMatch.Unknown, result.Observations[1].Actual);
    }

    [TestMethod]
    public void Run_ExpectedMissingField_CanBeVerifiedAlongsidePositiveAndNegative()
    {
        var result = SigmaReplay.Run(RuleSet(), "rules/login.yml",
            [new(Event("4625", "alice"), SigmaMatch.Yes), new(Event("4625", "bob"), SigmaMatch.No), new(Event("4625", null), SigmaMatch.Unknown)]);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(SigmaMatch.Unknown, result.Observations[2].Actual);
    }

    [TestMethod]
    public void Run_LocalException_ExcludesOnlyMatchingKnownFields()
    {
        var excluded = Event("4625", "alice");
        var included = excluded with { MessageId = Guid.CreateVersion7(), Content = excluded.Content.Replace("</EventData>", "<Data Name=\"Origin\">external</Data></EventData>") };
        var withoutExceptionField = Event("4625", "alice");
        excluded = excluded with { Content = excluded.Content.Replace("</EventData>", "<Data Name=\"Origin\">health-check</Data></EventData>") };
        var missing = Event("4625", null);
        var result = SigmaReplay.Run(RuleSet(), "rules/login.yml",
            [new(excluded, SigmaMatch.No), new(included, SigmaMatch.Yes), new(withoutExceptionField, SigmaMatch.Yes), new(missing, SigmaMatch.Unknown)],
            [new("Origin", "HEALTH-CHECK")]);
        Assert.IsTrue(result.Passed);
        Assert.AreEqual(SigmaMatch.No, result.Observations[0].Actual);
        Assert.AreEqual(SigmaMatch.Yes, result.Observations[1].Actual);
        Assert.AreEqual(SigmaMatch.Yes, result.Observations[2].Actual);
        Assert.AreEqual(SigmaMatch.Unknown, result.Observations[3].Actual);
    }

    [TestMethod]
    public void Run_RequiresPositiveAndNegativeExamples()
    {
        Assert.IsFalse(SigmaReplay.Run(RuleSet(), "rules/login.yml", [new(Event("4625", "alice"), SigmaMatch.Yes)]).Passed);
        Assert.IsFalse(SigmaReplay.Run(RuleSet(), "rules/login.yml", []).Passed);
    }

    [TestMethod]
    public void Run_StaleSource_IsNotTestedAsCurrent()
    {
        var snapshot = Snapshot() with { Availability = SigmaAvailability.Stale };
        Assert.ThrowsExactly<InvalidOperationException>(() => SigmaReplay.Run(new(snapshot, [Entry()], []), "rules/login.yml", []));
    }

    [TestMethod]
    public void Run_RuleNotInSelectedRevision_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SigmaReplay.Run(RuleSet(), "rules/other-revision.yml", []));
    }

    private static SigmaRuleSet RuleSet() => new(Snapshot(), [Entry()], []);

    private static SigmaSnapshot Snapshot() => new(SigmaAvailability.Ready, Guid.CreateVersion7(), Guid.CreateVersion7(), "v1", "commit", "digest", 1, 0);

    private static SigmaCompiledEntry Entry()
    {
        var result = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(Rule));
        Assert.IsNotNull(result.Rule, result.Error);
        return new(new("rules/login.yml", "Login failure", null, "author", "DRL-1.1", "license", "source", "digest"), result.Rule);
    }

    private static HostLogEntry Event(string eventId, string? user)
    {
        var userData = user is null ? "" : $"<Data Name=\"TargetUserName\">{user}</Data>";
        var xml = $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>{eventId}</EventID><Channel>Security</Channel></System><EventData>{userData}</EventData></Event>";
        return new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
            "test", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, xml, null, null, null, null, null, null);
    }
}
