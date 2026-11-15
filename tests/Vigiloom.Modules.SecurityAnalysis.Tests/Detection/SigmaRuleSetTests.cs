using System.Text;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaRuleSetTests
{
    private const string Rule = """
        title: Login failure
        name: failed_login
        logsource:
          product: windows
          service: security
        detection:
          selection:
            EventID: 4625
          condition: selection
        """;
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
    public async Task LoadSelected_SingleRule_DoesNotReadEverySourcePage()
    {
        var catalog = new Catalog();
        var loaded = await new SigmaRuleSetLoader(catalog).LoadSelectedAsync(["rules/a.yml"], CancellationToken.None);
        Assert.HasCount(1, loaded.Rules);
        Assert.AreEqual(1, catalog.RuleReads);
        Assert.AreEqual(0, catalog.PageReads);
    }

    [TestMethod]
    public async Task LoadSelected_Correlation_ResolvesFromFullSource()
    {
        var catalog = new Catalog(includeCorrelation: true);
        var loaded = await new SigmaRuleSetLoader(catalog).LoadSelectedAsync(["rules/b.yml"], CancellationToken.None);
        Assert.HasCount(1, loaded.Correlations);
        Assert.AreEqual(2, catalog.PageReads);
    }

    [TestMethod]
    public async Task Load_CompilesPagesAndReportsUnsupportedRules()
    {
        var loaded = await new SigmaRuleSetLoader(new Catalog()).LoadAsync(CancellationToken.None);
        Assert.AreEqual(SigmaAvailability.Ready, loaded.Snapshot.Availability);
        Assert.HasCount(1, loaded.Rules);
        Assert.HasCount(1, loaded.Rejected);
        Assert.AreEqual("rules/a.yml", loaded.Rules[0].Source.Path);
        Assert.Contains("Unsupported Sigma modifier", loaded.Rejected[0].Reason);
        Assert.HasCount(2, loaded.References);
        Assert.AreEqual(CorrelationReferenceStatus.Ambiguous, CorrelationReference.Resolve("failed_login", loaded.References).Status);
    }

    [TestMethod]
    public async Task Load_SourceChanges_DiscardsPartialResults()
    {
        var loaded = await new SigmaRuleSetLoader(new Catalog(changeOnSecondPage: true)).LoadAsync(CancellationToken.None);
        Assert.AreEqual(SigmaAvailability.SelectionChanged, loaded.Snapshot.Availability);
        Assert.IsEmpty(loaded.Rules);
        Assert.IsEmpty(loaded.Rejected);
    }

    [TestMethod]
    public async Task Load_Correlation_ResolvesReferenceAcrossPages()
    {
        var loaded = await new SigmaRuleSetLoader(new Catalog(includeCorrelation: true)).LoadAsync(CancellationToken.None);
        var correlation = Assert.ContainsSingle(loaded.Correlations);
        Assert.AreEqual("rules/b.yml", correlation.Source.Path);
        Assert.AreEqual("rules/a.yml", correlation.Plan.BaseRule.Source.Path);
        Assert.IsEmpty(loaded.Rejected);
    }

    private sealed class Catalog(bool changeOnSecondPage = false, bool includeCorrelation = false) : ISigmaCatalog
    {
        private readonly SigmaSnapshot snapshot = new(SigmaAvailability.Ready, Guid.CreateVersion7(), Guid.CreateVersion7(), "v1", "commit", "digest", 2, 0);
        public int PageReads { get; private set; }
        public int RuleReads { get; private set; }
        public Task<SigmaSnapshot> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task<SigmaSourceContentPage> ReadContentPageAsync(SigmaSnapshot selection, string? afterPath, CancellationToken cancellationToken)
        {
            PageReads++;
            if (afterPath is not null && changeOnSecondPage) return Task.FromResult(new SigmaSourceContentPage(SigmaAvailability.SelectionChanged, [], null));
            var first = afterPath is null;
            var path = first ? "rules/a.yml" : "rules/b.yml";
            var source = new SigmaSourceRule(path, "Login failure", null, "author", "DRL-1.1", "license", "source", "digest");
            var yaml = first ? Rule : includeCorrelation ? Correlation : Rule.Replace("EventID:", "EventID|re:", StringComparison.Ordinal);
            return Task.FromResult(new SigmaSourceContentPage(SigmaAvailability.Ready,
                [new(SigmaAvailability.Ready, source, Encoding.UTF8.GetBytes(yaml))], first ? path : null));
        }
        public Task<SigmaSourcePage> ReadPageAsync(SigmaSnapshot selection, string? afterPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SigmaSourceContent> ReadRuleAsync(SigmaSnapshot selection, string path, CancellationToken cancellationToken)
        {
            RuleReads++;
            var source = new SigmaSourceRule(path, "Login failure", null, "author", "DRL-1.1", "license", "source", "digest");
            var yaml = path == "rules/a.yml" ? Rule : includeCorrelation ? Correlation : Rule.Replace("EventID:", "EventID|re:", StringComparison.Ordinal);
            return Task.FromResult(new SigmaSourceContent(SigmaAvailability.Ready, source, Encoding.UTF8.GetBytes(yaml)));
        }
    }
}
