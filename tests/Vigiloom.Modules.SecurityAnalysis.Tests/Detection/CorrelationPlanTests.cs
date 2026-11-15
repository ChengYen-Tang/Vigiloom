using System.Text;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class CorrelationPlanTests
{
    private const string Correlation = """
        title: Multiple failed logins
        correlation:
          type: event_count
          rules: [failed_login]
          group-by: [TargetUserName]
          timespan: 5m
          condition: {gte: 3}
        """;

    [TestMethod]
    public void Create_ReadySource_ResolvesItsBaseRule()
    {
        var rule = BaseRule();
        var result = CorrelationPlanner.Create(Encoding.UTF8.GetBytes(Correlation),
            new(Snapshot(), [rule], []) { References = [new(null, "failed_login", rule)] }, TimeSpan.FromDays(1));

        Assert.IsNotNull(result.Plan, result.Error);
        Assert.AreSame(rule, result.Plan.BaseRule);
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Plan.Rule.Window);
    }

    [TestMethod]
    public void Create_StaleSourceOrAmbiguousReference_DoesNotBuildPlan()
    {
        var rule = BaseRule();
        var candidates = new CorrelationCandidate[] { new(null, "failed_login", rule), new(null, "failed_login", null) };
        var stale = new SigmaRuleSet(Snapshot() with { Availability = SigmaAvailability.Stale }, [rule], []) { References = candidates };
        var ambiguous = new SigmaRuleSet(Snapshot(), [rule], []) { References = candidates };
        Assert.IsNull(CorrelationPlanner.Create(Encoding.UTF8.GetBytes(Correlation), stale, TimeSpan.FromDays(1)).Plan);
        var result = CorrelationPlanner.Create(Encoding.UTF8.GetBytes(Correlation), ambiguous, TimeSpan.FromDays(1));
        Assert.IsNull(result.Plan);
        Assert.IsNotNull(result.Error);
        Assert.Contains("ambiguous", result.Error);
    }

    private static SigmaSnapshot Snapshot() => new(SigmaAvailability.Ready, Guid.CreateVersion7(), Guid.CreateVersion7(), "v1", "commit", "digest", 1, 0);

    private static SigmaCompiledEntry BaseRule()
    {
        const string yaml = """
            title: Failed login
            name: failed_login
            logsource: {product: windows, service: security}
            detection:
              selection: {EventID: 4625}
              condition: selection
            """;
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        return new(new("rules/login.yml", "Failed login", null, "author", "DRL-1.1", "license", "source", "digest"), compiled.Rule);
    }
}
