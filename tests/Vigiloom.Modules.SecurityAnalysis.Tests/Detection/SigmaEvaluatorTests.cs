using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaEvaluatorTests(TestContext context)
{
    [TestMethod]
    public void Evaluate_UsesOneEventForSelectedRulesAndKeepsUnknownDistinct()
    {
        var snapshot = Snapshot(SigmaAvailability.Ready);
        var rules = new SigmaRuleSet(snapshot, [Rule("EventID", "4625"), Rule("TargetUserName", "alice")], []);
        var log = Event();

        var result = SigmaEvaluator.Evaluate(rules, log, context.CancellationToken);

        Assert.AreEqual(log.AgentId, result.AgentId);
        Assert.AreEqual(log.MessageId, result.MessageId);
        Assert.AreEqual(snapshot.RevisionId, result.Snapshot.RevisionId);
        Assert.HasCount(2, result.Rules);
        Assert.AreEqual(SigmaMatch.Yes, result.Rules[0].Result);
        Assert.AreEqual(SigmaMatch.Unknown, result.Rules[1].Result);
    }

    [TestMethod]
    public void Evaluate_UnavailableSource_DoesNotReportRuleResults()
    {
        var rules = new SigmaRuleSet(Snapshot(SigmaAvailability.Stale), [Rule("EventID", "4625")], []);
        var result = SigmaEvaluator.Evaluate(rules, Event(), context.CancellationToken);
        Assert.AreEqual(SigmaAvailability.Stale, result.Snapshot.Availability);
        Assert.IsEmpty(result.Rules);
    }

    [TestMethod]
    public void Evaluate_CollectionGap_IsNotReportedAsNoMatch()
    {
        var rules = new SigmaRuleSet(Snapshot(SigmaAvailability.Ready), [Rule("EventID", "4625")], []);
        var log = Event() with { Gap = new(AgentLogGapReason.CursorLost, null) };
        var result = SigmaEvaluator.Evaluate(rules, log, context.CancellationToken);
        Assert.AreEqual(SigmaMatch.Unknown, result.Rules[0].Result);
    }

    [TestMethod]
    public void Evaluate_Cancelled_StopsRuleEvaluation()
    {
        var rules = new SigmaRuleSet(Snapshot(SigmaAvailability.Ready), [Rule("EventID", "4625")], []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SigmaEvaluator.Evaluate(rules, Event(), cancellation.Token));
    }

    private static SigmaSnapshot Snapshot(SigmaAvailability availability) =>
        new(availability, Guid.CreateVersion7(), Guid.CreateVersion7(), "v1", "commit", "digest", 2, 0);

    private static SigmaCompiledEntry Rule(string field, string value)
    {
        var yaml = $"title: Test\nlogsource:\n  product: windows\n  service: security\ndetection:\n  selection:\n    {field}: {value}\n  condition: selection\n";
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        return new(new($"rules/{field}.yml", "Test", null, "author", "DRL-1.1", "license", "source", "digest"), compiled.Rule);
    }

    private static HostLogEntry Event()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>4625</EventID><Channel>Security</Channel></System></Event>
            """;
        return new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
            "test", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, xml, null, null, null, null, null, null);
    }
}
