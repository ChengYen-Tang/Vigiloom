using System.Text;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class CorrelationReferenceTests
{
    [TestMethod]
    public void Resolve_ByIdOrName_UsesTheSameSupportedRule()
    {
        var rule = Rule();
        var candidates = new[] { new CorrelationCandidate(rule.Rule.Metadata.Id, rule.Rule.Metadata.Name, rule) };
        Assert.AreEqual(CorrelationReferenceStatus.Ready, CorrelationReference.Resolve("AA-BB", candidates).Status);
        Assert.AreSame(rule, CorrelationReference.Resolve("failed_login", candidates).Rule);
        Assert.AreEqual(CorrelationReferenceStatus.Missing, CorrelationReference.Resolve("FAILED_LOGIN", candidates).Status);
    }

    [TestMethod]
    public void Resolve_AmbiguousOrUnsupported_DoesNotChooseAnotherRule()
    {
        var rule = Rule();
        var ambiguous = new[]
        {
            new CorrelationCandidate(rule.Rule.Metadata.Id, rule.Rule.Metadata.Name, rule),
            new CorrelationCandidate("different", "failed_login", null)
        };
        Assert.AreEqual(CorrelationReferenceStatus.Ambiguous, CorrelationReference.Resolve("failed_login", ambiguous).Status);
        Assert.AreEqual(CorrelationReferenceStatus.Unsupported,
            CorrelationReference.Resolve("different", [new("different", null, null)]).Status);
        Assert.AreEqual(CorrelationReferenceStatus.Missing, CorrelationReference.Resolve("missing", ambiguous).Status);
    }

    private static SigmaCompiledEntry Rule()
    {
        const string yaml = """
            title: Failed login
            id: aa-bb
            name: failed_login
            logsource: {product: windows, service: security}
            detection:
              selection: {EventID: 4625}
              condition: selection
            """;
        var result = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(result.Rule, result.Error);
        return new(new("rules/example.yml", "Failed login", "aa-bb", "author", "DRL-1.1", "license", "source", "digest"), result.Rule);
    }
}
