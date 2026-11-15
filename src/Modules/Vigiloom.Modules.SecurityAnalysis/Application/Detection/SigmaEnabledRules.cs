using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record SigmaEnabledRuleVersion(Guid Id, Guid TestId, string Path, byte[] Version,
    IReadOnlyList<SigmaRuleException> Exceptions);
internal sealed record SigmaEnabledRuleSet(SigmaRuleSet Rules, IReadOnlyList<SigmaEnabledRuleVersion> Versions,
    IReadOnlyList<string> UnavailablePaths);

/// <summary>Restricts a verified source snapshot to rules explicitly enabled against that exact version.</summary>
internal sealed class SigmaEnabledRules(IDbContextFactory<SecurityAnalysisDbContext> contexts, SigmaRuleSetLoader loader)
{
    internal async Task<SigmaEnabledRuleSet> LoadAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var enabled = await (from activation in db.SigmaRuleActivations.AsNoTracking()
            join test in db.SigmaRuleTests.AsNoTracking() on activation.TestId equals test.Id
            where activation.Enabled
            select new { activation.Id, activation.SourceId, activation.TestId, activation.Version, ActivationPath = activation.Path, TestSourceId = test.SourceId, test.RevisionId, test.ActivationVersion,
                TestPath = test.Path, test.Digest, test.MatcherVersion, test.Passed, test.SamplesJson }).ToArrayAsync(token);
        var rules = await loader.LoadSelectedAsync(enabled.Select(x => x.ActivationPath).ToArray(), token);
        if (rules.Snapshot.Availability != SigmaAvailability.Ready || rules.Snapshot.SourceId is not { } sourceId)
            return new(new SigmaRuleSet(rules.Snapshot, [], rules.Rejected) { References = rules.References }, [], []);
        var byPath = rules.Rules.ToDictionary(x => x.Source.Path, StringComparer.Ordinal);
        var correlationsByPath = rules.Correlations.ToDictionary(x => x.Source.Path, StringComparer.Ordinal);
        var selected = new List<SigmaCompiledEntry>(enabled.Length);
        var selectedCorrelations = new List<SigmaCorrelationEntry>();
        var versions = new List<SigmaEnabledRuleVersion>(enabled.Length);
        var unavailable = new List<string>();
        foreach (var entry in enabled)
        {
            if (entry.SourceId != sourceId) continue;
            if (entry.TestSourceId != sourceId || !string.Equals(entry.ActivationPath, entry.TestPath, StringComparison.Ordinal) ||
                !entry.Passed || entry.RevisionId != rules.Snapshot.RevisionId ||
                entry.ActivationVersion != rules.Snapshot.ActivationVersion || entry.MatcherVersion != SigmaTesting.MatcherVersion ||
                !SigmaExceptions.TryRead(entry.SamplesJson, out var exceptions))
            {
                unavailable.Add(entry.ActivationPath);
                continue;
            }
            if (byPath.TryGetValue(entry.ActivationPath, out var rule) && string.Equals(entry.Digest, rule.Source.Sha256, StringComparison.Ordinal))
                selected.Add(rule);
            else if (correlationsByPath.TryGetValue(entry.ActivationPath, out var correlation) &&
                string.Equals(entry.Digest, correlation.Source.Sha256, StringComparison.Ordinal) &&
                CorrelationTestEvidence.Matches(entry.SamplesJson, correlation))
                selectedCorrelations.Add(correlation);
            else
            {
                unavailable.Add(entry.ActivationPath);
                continue;
            }
            versions.Add(new(entry.Id, entry.TestId, entry.ActivationPath, entry.Version, exceptions));
        }
        return new(new SigmaRuleSet(rules.Snapshot, selected, rules.Rejected)
            { References = rules.References, Correlations = selectedCorrelations }, versions, unavailable);
    }

    /// <summary>Fences a pending result against source and rule changes in the caller's transaction.</summary>
    internal static async Task<bool> StillCurrentAsync(SecurityAnalysisDbContext db, SigmaCatalog catalog,
        SigmaEnabledRuleSet selection, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || selection.Rules.Snapshot.SourceId is not { } sourceId)
            throw new InvalidOperationException("A transaction and selected source are required.");
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (source is null || await catalog.CheckSelectionAsync(db, selection.Rules.Snapshot, token) != SigmaAvailability.Ready) return false;
        var ids = selection.Versions.Select(x => x.Id).ToArray();
        var current = await db.SigmaRuleActivations.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.TestId, x.Path, x.Enabled, x.Version }).ToDictionaryAsync(x => x.Id, token);
        return selection.Versions.All(expected => current.TryGetValue(expected.Id, out var actual) && actual.Enabled &&
            actual.TestId == expected.TestId && string.Equals(actual.Path, expected.Path, StringComparison.Ordinal) &&
            actual.Version.AsSpan().SequenceEqual(expected.Version));
    }
}
