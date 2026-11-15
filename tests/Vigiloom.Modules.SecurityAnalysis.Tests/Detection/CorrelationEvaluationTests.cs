using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class CorrelationEvaluationTests(TestContext context)
{
    [TestMethod]
    public void Evaluate_LateEvent_CanMakeAnEarlierProcessedAnchorMatch()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var late = Event(agent, now, now.AddMinutes(2), "alice");
        var middle = Event(agent, now.AddMinutes(1), now.AddMinutes(1), "alice");
        var anchor = Event(agent, now.AddMinutes(2), now.AddMinutes(2), "alice");
        var result = Run([anchor, middle, late, late]);

        Assert.HasCount(3, result);
        var found = result.Single(x => x.Event.MessageId == anchor.MessageId);
        Assert.AreEqual(SigmaMatch.Yes, found.Result);
        Assert.AreEqual(3, found.Match!.Count);
        Assert.AreEqual(now, found.Match.FirstEventAt);
    }

    [TestMethod]
    public void Evaluate_LocalException_ExcludesEventBeforeCounting()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var noise = Event(agent, now, now, "alice");
        noise = noise with { Content = noise.Content.Replace("</EventData>", "<Data Name=\"Origin\">health-check</Data></EventData>") };
        var second = Event(agent, now.AddSeconds(1), now.AddSeconds(1), "alice");
        var third = Event(agent, now.AddSeconds(2), now.AddSeconds(2), "alice");
        var events = new[] { noise, second, third };
        Assert.AreEqual(SigmaMatch.Yes, Run(events).Single(x => x.Event.MessageId == third.MessageId).Result);
        var filtered = CorrelationEvaluation.Evaluate(Plan(), events, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20,
            context.CancellationToken, [new("Origin", "health-check")]);
        Assert.AreEqual(SigmaMatch.No, filtered.Observations.Single(x => x.Event.MessageId == noise.MessageId).Result);
        Assert.AreEqual(SigmaMatch.No, filtered.Observations.Single(x => x.Event.MessageId == third.MessageId).Result);
    }

    [TestMethod]
    public void Evaluate_MissingGroup_MakesPotentialNegativeUnknown()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var missing = Event(agent, now, now, null);
        var known = Event(agent, now.AddMinutes(1), now.AddMinutes(1), "alice");
        var result = Run([missing, known]);

        Assert.AreEqual(SigmaMatch.Unknown, result.Single(x => x.Event.MessageId == missing.MessageId).Result);
        Assert.AreEqual(SigmaMatch.Unknown, result.Single(x => x.Event.MessageId == known.MessageId).Result);
    }

    [TestMethod]
    public void Evaluate_DifferentGroupsAndCapacity_AreNotFalseMatches()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var events = new[] { Event(agent, now, now, "alice"), Event(agent, now, now, "bob") };
        Assert.IsTrue(Run(events).All(x => x.Result == SigmaMatch.No));
        var limited = CorrelationEvaluation.Evaluate(Plan(), events, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 1, context.CancellationToken);
        Assert.IsTrue(limited.CapacityExceeded);
        Assert.IsEmpty(limited.Observations);
    }

    [TestMethod]
    public void Evaluate_LateEventInAnotherGroup_DoesNotMakeThisGroupUnknown()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var lateAlice = Event(agent, now, now.AddMinutes(6), "alice");
        var bob = Event(agent, now.AddMinutes(1), now.AddMinutes(1), "bob");
        var result = Run([lateAlice, bob]);

        Assert.AreEqual(SigmaMatch.Unknown, result.Single(x => x.Event.MessageId == lateAlice.MessageId).Result);
        Assert.AreEqual(SigmaMatch.No, result.Single(x => x.Event.MessageId == bob.MessageId).Result);
    }

    [TestMethod]
    public void ApplyCoverage_IncompleteWindow_DoesNotTurnAConfirmedMatchIntoUnknown()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var first = Event(agent, now, now, "alice");
        var second = Event(agent, now.AddSeconds(1), now.AddSeconds(1), "alice");
        var third = Event(agent, now.AddSeconds(2), now.AddSeconds(2), "alice");
        var evaluation = CorrelationEvaluation.Evaluate(Plan(), [first, second, third], TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(30), 20, context.CancellationToken);
        var covered = CorrelationEvaluation.ApplyCoverage(evaluation, TimeSpan.FromMinutes(2), now.AddSeconds(-1), now.AddMinutes(1));
        Assert.AreEqual(SigmaMatch.Unknown, covered.Observations.Single(x => x.Event.MessageId == first.MessageId).Result);
        Assert.AreEqual(SigmaMatch.Yes, covered.Observations.Single(x => x.Event.MessageId == third.MessageId).Result);
    }

    [TestMethod]
    public void Replay_MatchNonMatchAndMissingFields_UsesSameEvaluation()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var agent = Guid.CreateVersion7();
        var first = Event(agent, now, now, "alice");
        var second = Event(agent, now.AddSeconds(1), now.AddSeconds(1), "alice");
        var anchor = Event(agent, now.AddSeconds(2), now.AddSeconds(2), "alice");
        var missing = Event(agent, now.AddMinutes(10), now.AddMinutes(10), null);
        var plan = Plan();
        var snapshot = new SigmaSnapshot(SigmaAvailability.Ready, Guid.CreateVersion7(), Guid.CreateVersion7(), "v1", "commit", "digest", 1, 0);
        var samples = new SigmaReplaySample[]
        {
            new(first, SigmaMatch.No), new(second, SigmaMatch.No), new(anchor, SigmaMatch.Yes), new(missing, SigmaMatch.Unknown)
        };
        var ruleSet = new SigmaRuleSet(snapshot, [plan.BaseRule], []) { Correlations = [new(plan.BaseRule.Source, plan)] };
        var result = SigmaReplay.RunCorrelation(ruleSet, plan.BaseRule.Source.Path, samples,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20, context.CancellationToken);
        Assert.IsTrue(result.Passed);
        Assert.AreEqual(SigmaMatch.Yes, result.Observations[2].Actual);
        Assert.AreEqual(SigmaMatch.Unknown, result.Observations[3].Actual);
        Assert.ThrowsExactly<ArgumentException>(() => SigmaReplay.RunCorrelation(ruleSet, "another-source.yml", samples,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20, context.CancellationToken));
    }

    private IReadOnlyList<CorrelationObservation> Run(IReadOnlyList<HostLogEntry> events, int maximumEvents = 20) =>
        CorrelationEvaluation.Evaluate(Plan(), events, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), maximumEvents, context.CancellationToken).Observations;

    private static CorrelationPlan Plan()
    {
        const string yaml = """
            title: Failed login
            logsource: {product: windows, service: security}
            detection:
              selection: {EventID: 4625}
              condition: selection
            """;
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        var source = new SigmaSourceRule("rules/login.yml", "Failed login", null, "author", "DRL-1.1", "license", "source", "digest");
        return new(new("login", ["TargetUserName"], TimeSpan.FromMinutes(2), 3), new(source, compiled.Rule));
    }

    private static HostLogEntry Event(Guid agent, DateTimeOffset? occurred, DateTimeOffset received, string? user)
    {
        var userData = user is null ? "" : $"<Data Name=\"TargetUserName\">{user}</Data>";
        var xml = $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4625</EventID><Channel>Security</Channel></System><EventData>{userData}</EventData></Event>";
        return new(agent, Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
            "test", 1, occurred, received, xml, null, null, null, null, null, null);
    }
}
