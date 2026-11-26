using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

public sealed record SigmaEvidence(Guid RevisionId, string Path, string Content,
    IReadOnlyList<SigmaRuleException>? Exceptions, bool BaseSpecified, string? BaseRule);

public sealed class SigmaEvidenceReader(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    public async Task<SigmaEvidence?> GetAsync(Guid testId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var evidence = await (from test in db.SigmaRuleTests.AsNoTracking()
            join rule in db.SigmaRules.AsNoTracking() on new { test.RevisionId, test.Path } equals new { rule.RevisionId, rule.Path }
            where test.Id == testId
            select new { test.RevisionId, test.Path, test.Digest, test.SamplesJson, rule.Content, RuleDigest = rule.Digest }).SingleOrDefaultAsync(token);
        if (evidence is null || !string.Equals(evidence.Digest, Convert.ToHexStringLower(evidence.RuleDigest), StringComparison.OrdinalIgnoreCase)) return null;
        IReadOnlyList<SigmaRuleException>? exceptions = SigmaExceptions.TryRead(evidence.SamplesJson, out var items) ? items : null;
        var baseSpecified = false;
        string? baseRule = null;
        using var json = JsonDocument.Parse(evidence.SamplesJson);
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("BasePath", out var path))
        {
            baseSpecified = true;
            if (path.ValueKind == JsonValueKind.String && json.RootElement.TryGetProperty("BaseSha256", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                var basePath = path.GetString();
                var source = await db.SigmaRules.AsNoTracking().Where(x => x.RevisionId == evidence.RevisionId && x.Path == basePath)
                    .Select(x => new { x.Content, x.Digest }).SingleOrDefaultAsync(token);
                if (source is not null && string.Equals(digest.GetString(), Convert.ToHexStringLower(source.Digest), StringComparison.OrdinalIgnoreCase))
                    baseRule = Encoding.UTF8.GetString(source.Content);
            }
        }
        return new(evidence.RevisionId, evidence.Path, Encoding.UTF8.GetString(evidence.Content), exceptions, baseSpecified, baseRule);
    }
}
