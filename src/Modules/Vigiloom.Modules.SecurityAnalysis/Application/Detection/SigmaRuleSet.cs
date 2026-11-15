using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record SigmaCompiledEntry(SigmaSourceRule Source, CompiledSigmaRule Rule);
internal sealed record SigmaCorrelationEntry(SigmaSourceRule Source, CorrelationPlan Plan);
internal sealed record SigmaRejectedEntry(SigmaSourceRule Source, string Reason);
internal sealed record SigmaRuleSet(SigmaSnapshot Snapshot, IReadOnlyList<SigmaCompiledEntry> Rules, IReadOnlyList<SigmaRejectedEntry> Rejected)
{
    internal IReadOnlyList<CorrelationCandidate> References { get; init; } = [];
    internal IReadOnlyList<SigmaCorrelationEntry> Correlations { get; init; } = [];
}

/// <summary>Compiles the current attributed source; a changed source cannot yield a partial rule set.</summary>
internal sealed class SigmaRuleSetLoader(ISigmaCatalog catalog)
{
    internal async Task<SigmaRuleSet> LoadSelectedAsync(IReadOnlyCollection<string> paths, CancellationToken token)
    {
        var snapshot = await catalog.GetActiveAsync(token);
        if (snapshot.Availability != SigmaAvailability.Ready) return new(snapshot, [], []);
        var accepted = new List<SigmaCompiledEntry>(paths.Count);
        var rejected = new List<SigmaRejectedEntry>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var content = await catalog.ReadRuleAsync(snapshot, path, token);
            if (content.Availability == SigmaAvailability.Unavailable) continue;
            if (content.Availability != SigmaAvailability.Ready)
                return new(snapshot with { Availability = content.Availability }, [], []);
            if (content.Rule is null || content.Content is null)
                return new(snapshot with { Availability = SigmaAvailability.Unavailable }, [], []);
            var metadata = SigmaRuleValidator.Validate(content.Content);
            if (metadata.IsCorrelation)
            {
                var full = await LoadAsync(token);
                return full.Snapshot == snapshot ? full : new(snapshot with { Availability = SigmaAvailability.SelectionChanged }, [], []);
            }
            var compiled = SigmaRuleCompiler.Compile(content.Content, metadata);
            if (compiled.Rule is { } rule) accepted.Add(new(content.Rule, rule));
            else rejected.Add(new(content.Rule, compiled.Error ?? "Unsupported rule."));
        }
        return await catalog.GetActiveAsync(token) == snapshot
            ? new(snapshot, accepted, rejected) : new(snapshot with { Availability = SigmaAvailability.SelectionChanged }, [], []);
    }

    internal async Task<SigmaRuleSet> LoadAsync(CancellationToken token)
    {
        var snapshot = await catalog.GetActiveAsync(token);
        if (snapshot.Availability != SigmaAvailability.Ready) return new(snapshot, [], []);
        var accepted = new List<SigmaCompiledEntry>();
        var rejected = new List<SigmaRejectedEntry>();
        var references = new List<CorrelationCandidate>();
        var correlationSources = new List<(SigmaSourceRule Source, EventCountRule Rule)>();
        string? cursor = null;
        do
        {
            var page = await catalog.ReadContentPageAsync(snapshot, cursor, token);
            if (page.Availability != SigmaAvailability.Ready)
                return new(snapshot with { Availability = page.Availability }, [], []);
            foreach (var item in page.Rules)
            {
                token.ThrowIfCancellationRequested();
                if (item.Rule is null || item.Content is null || item.Availability != SigmaAvailability.Ready)
                    return new(snapshot with { Availability = SigmaAvailability.Unavailable }, [], []);
                var metadata = SigmaRuleValidator.Validate(item.Content);
                if (metadata.IsCorrelation)
                {
                    references.Add(new(metadata.Id, metadata.Name, null));
                    var correlation = CorrelationCompiler.Compile(item.Content, TimeSpan.FromDays(7));
                    if (correlation.Rule is { } parsed) correlationSources.Add((item.Rule, parsed));
                    else rejected.Add(new(item.Rule, correlation.Error ?? "Unsupported correlation."));
                    continue;
                }
                var compiled = SigmaRuleCompiler.Compile(item.Content, metadata);
                SigmaCompiledEntry? entry = compiled.Rule is { } rule ? new(item.Rule, rule) : null;
                references.Add(new(metadata.Id, metadata.Name, entry));
                if (entry is not null) accepted.Add(entry);
                else rejected.Add(new(item.Rule, compiled.Error ?? "Unsupported rule."));
            }
            cursor = page.NextPath;
        } while (cursor is not null);
        var ruleSet = new SigmaRuleSet(snapshot, accepted, rejected) { References = references };
        var correlations = new List<SigmaCorrelationEntry>(correlationSources.Count);
        foreach (var (source, correlation) in correlationSources)
        {
            token.ThrowIfCancellationRequested();
            var plan = CorrelationPlanner.Resolve(correlation, ruleSet);
            if (plan.Plan is { } ready) correlations.Add(new(source, ready));
            else rejected.Add(new(source, plan.Error ?? "Unsupported correlation."));
        }
        return ruleSet with { Correlations = correlations };
    }
}
