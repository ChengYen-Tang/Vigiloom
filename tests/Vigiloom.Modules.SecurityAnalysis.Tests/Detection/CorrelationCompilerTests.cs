using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class CorrelationCompilerTests
{
    private const string Rule = """
        title: Multiple failed logons
        id: 0e95725d-7320-415d-80f7-004da920fc11
        author: Example
        correlation:
          type: event_count
          rules:
            - failed_login
          group-by:
            - TargetUserName
            - TargetDomainName
          timespan: 5m
          condition:
            gte: 10
        """;

    [TestMethod]
    public void Compile_EventCount_PreservesReferenceGroupsWindowAndThreshold()
    {
        var bytes = Encoding.UTF8.GetBytes(Rule);
        Assert.IsTrue(SigmaRuleValidator.Validate(bytes).IsCorrelation);
        var result = CorrelationCompiler.Compile(bytes, TimeSpan.FromDays(7));
        Assert.IsNotNull(result.Rule, result.Error);
        Assert.AreEqual("failed_login", result.Rule.Reference);
        Assert.HasCount(2, result.Rule.GroupBy);
        Assert.AreEqual("TargetUserName", result.Rule.GroupBy[0]);
        Assert.AreEqual("TargetDomainName", result.Rule.GroupBy[1]);
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Rule.Window);
        Assert.AreEqual(10, result.Rule.MinimumCount);
    }

    [TestMethod]
    [DataRow(256, true)]
    [DataRow(257, false)]
    public void Compile_ReferenceName_RespectsSigmaLengthLimit(int length, bool supported)
    {
        var name = new string('a', length);
        var yaml = Rule.Replace("failed_login", name, StringComparison.Ordinal);
        var result = CorrelationCompiler.Compile(Encoding.UTF8.GetBytes(yaml), TimeSpan.FromDays(7));
        Assert.AreEqual(supported, result.Rule is not null);
    }

    [TestMethod]
    [DataRow("type: event_count", "type: value_count")]
    [DataRow("gte: 10", "lt: 10")]
    [DataRow("timespan: 5m", "timespan: 8d")]
    [DataRow("timespan: 5m", "timespan: 5M")]
    [DataRow("- failed_login", "- null")]
    [DataRow("title: Multiple failed logons", "title: Multiple failed logons\ngenerate: true")]
    [DataRow("type: event_count", "type: event_count\n  generate: true")]
    public void Compile_UnsupportedSemantics_AreRejected(string before, string after)
    {
        var yaml = Rule.Replace(before, after, StringComparison.Ordinal);
        var result = CorrelationCompiler.Compile(Encoding.UTF8.GetBytes(yaml), TimeSpan.FromDays(7));
        Assert.IsNull(result.Rule);
        Assert.IsNotNull(result.Error);
    }
}
