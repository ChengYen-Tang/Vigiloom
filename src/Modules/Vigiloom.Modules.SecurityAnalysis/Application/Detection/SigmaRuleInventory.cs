using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Lists a bounded page of attributed rules and their local test and activation state.</summary>
public sealed class SigmaRuleInventory(IDbContextFactory<SecurityAnalysisDbContext> contexts, ISigmaCatalog catalog)
{
    private sealed record Cursor(Guid SourceId, Guid RevisionId, string ActivationVersion, string AfterPath);

    public static bool ValidCursor(string? cursor) => cursor is null || TryReadCursor(cursor, out _);

    public async Task<SigmaDetectionRulePage> GetAsync(string? cursor, CancellationToken token)
    {
        if (cursor is not null && !TryReadCursor(cursor, out _)) throw new ArgumentException("Invalid rule cursor.", nameof(cursor));
        Cursor? position = null;
        if (cursor is not null) TryReadCursor(cursor, out position);
        var snapshot = await catalog.GetActiveAsync(token);
        if (snapshot.Availability != SigmaAvailability.Ready) return new(snapshot, [], null);
        if (position is not null && (position.SourceId != snapshot.SourceId || position.RevisionId != snapshot.RevisionId ||
            position.ActivationVersion != snapshot.ActivationVersion))
            return new(snapshot with { Availability = SigmaAvailability.SelectionChanged }, [], null);
        var page = await catalog.ReadContentPageAsync(snapshot, position?.AfterPath, token);
        if (page.Availability != SigmaAvailability.Ready) return new(snapshot with { Availability = page.Availability }, [], null);
        if (page.Rules.Any(x => x.Rule is null || x.Content is null || x.Availability != SigmaAvailability.Ready))
            return new(snapshot with { Availability = SigmaAvailability.Unavailable }, [], null);
        SigmaRuleSet? correlations = null;
        if (page.Rules.Any(x => SigmaRuleValidator.Validate(x.Content!).IsCorrelation))
        {
            correlations = await new SigmaRuleSetLoader(catalog).LoadAsync(token);
            if (correlations.Snapshot != snapshot) return new(correlations.Snapshot, [], null);
        }
        var paths = page.Rules.Select(x => x.Rule!.Path).ToArray();
        await using var db = await contexts.CreateDbContextAsync(token);
        var sourceId = snapshot.SourceId!.Value;
        var revisionId = snapshot.RevisionId!.Value;
        var settings = await (from setting in db.SigmaRuleActivations.AsNoTracking()
            join test in db.SigmaRuleTests.AsNoTracking() on setting.TestId equals test.Id
            where setting.SourceId == sourceId && paths.Contains(setting.Path)
            select new { setting.Path, setting.Enabled, setting.Version, setting.TestId, TestPath = test.Path, test.SourceId, test.RevisionId,
                test.ActivationVersion, test.MatcherVersion, test.Digest, test.Passed, test.SamplesJson }).ToDictionaryAsync(x => x.Path, StringComparer.Ordinal, token);
        var latest = await db.SigmaRuleTests.AsNoTracking()
            .Where(x => x.SourceId == sourceId && x.RevisionId == revisionId && x.ActivationVersion == snapshot.ActivationVersion &&
                x.MatcherVersion == SigmaTesting.MatcherVersion && paths.Contains(x.Path))
            .GroupBy(x => x.Path).Select(g => g.OrderByDescending(x => x.TestedAt).ThenByDescending(x => x.RowId)
                .Select(x => new { x.Path, x.Id, x.Passed, x.Digest, x.SamplesJson }).First()).ToDictionaryAsync(x => x.Path, StringComparer.Ordinal, token);
        var items = new List<SigmaDetectionRuleItem>(page.Rules.Count);
        foreach (var item in page.Rules)
        {
            if (item.Rule is null || item.Content is null) return new(snapshot with { Availability = SigmaAvailability.Unavailable }, [], null);
            var rule = item.Rule;
            var metadata = SigmaRuleValidator.Validate(item.Content);
            var compiled = metadata.IsCorrelation ? null : SigmaRuleCompiler.Compile(item.Content, metadata);
            var correlation = metadata.IsCorrelation ? correlations!.Correlations.SingleOrDefault(x => x.Source.Path == rule.Path) : null;
            var supported = compiled?.Rule is not null || correlation is not null;
            var reason = metadata.IsCorrelation ? correlations!.Rejected.FirstOrDefault(x => x.Source.Path == rule.Path)?.Reason : compiled?.Error;
            settings.TryGetValue(rule.Path, out var setting);
            latest.TryGetValue(rule.Path, out var test);
            if (test is not null && test.Digest != rule.Sha256) test = null;
            var current = setting is { Enabled: true, Passed: true } && supported &&
                setting.SourceId == sourceId && setting.RevisionId == revisionId && setting.ActivationVersion == snapshot.ActivationVersion &&
                setting.MatcherVersion == SigmaTesting.MatcherVersion && setting.TestPath == rule.Path && setting.Digest == rule.Sha256 &&
                SigmaExceptions.TryRead(setting.SamplesJson, out _) &&
                (correlation is null || CorrelationTestEvidence.Matches(setting.SamplesJson, correlation));
            IReadOnlyList<SigmaRuleException> latestExceptions = [];
            if (test is not null && !SigmaExceptions.TryRead(test.SamplesJson, out latestExceptions)) test = null;
            IReadOnlyList<SigmaRuleException> activeExceptions = [];
            if (setting is not null) SigmaExceptions.TryRead(setting.SamplesJson, out activeExceptions);
            items.Add(new(rule, supported, reason, test?.Id, test?.Passed,
                setting?.Enabled ?? false, current, setting is null ? "" : Convert.ToBase64String(setting.Version), latestExceptions, setting?.TestId, activeExceptions));
        }
        if (await catalog.GetActiveAsync(token) != snapshot) return new(snapshot with { Availability = SigmaAvailability.SelectionChanged }, [], null);
        var next = page.NextPath is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(sourceId, revisionId, snapshot.ActivationVersion!, page.NextPath)));
        return new(snapshot, items, next);
    }

    public async Task<SigmaRuleStatePage> GetStatesAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.SigmaRuleActivations.AsNoTracking().OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(51)
            .Select(x => new { x.SourceId, x.Path, x.TestId, x.Enabled, x.Version }).ToArrayAsync(token);
        return new(rows.Take(50).Select(x => new SigmaRuleStateItem(x.SourceId, x.Path, x.TestId, x.Enabled,
            Convert.ToBase64String(x.Version))).ToArray(), page, rows.Length > 50 && page < 1000);
    }

    private static bool TryReadCursor(string value, out Cursor? cursor)
    {
        cursor = null;
        if (value.Length is < 1 or > 2048) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value));
            return cursor is { SourceId.Version: 7, RevisionId.Version: 7, ActivationVersion.Length: > 0 and <= 32,
                AfterPath.Length: > 0 and <= 512 };
        }
        catch (Exception error) when (error is FormatException or JsonException or NotSupportedException or DecoderFallbackException)
        {
            return false;
        }
    }
}
