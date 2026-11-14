using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Bounded non-secret intelligence status and retained revision metadata.</summary>
public sealed class IntelligenceStatus(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 50;

    public async Task<AdvisoryImportProgressView?> GetAdvisoryProgressAsync(Guid sourceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (source is null || source.Key != "github-reviewed") return null;
        var import = await db.AdvisoryImports.AsNoTracking().Where(x => x.SourceId == sourceId)
            .OrderBy(x => x.Phase < 4 ? 0 : 1).ThenByDescending(x => x.StartedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
        var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == sourceId)
            .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(token);
        var now = clock.GetUtcNow();
        var retryAt = await db.AdvisoryImports.AsNoTracking().Where(x => x.SourceId == sourceId && x.NotBefore > now)
            .MaxAsync(x => x.NotBefore, token);
        var eligible = import?.ResultRevisionId is { } revisionId &&
            await AdvisoryRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token);
        var state = import switch
        {
            _ when !source.Enabled => "disabled",
            null => "not-started",
            { Phase: 3, ReconciliationPhase: 4 } => "inconsistent",
            { FailureCode: "invalid-response" or "cursor-rejected" or "limit-exceeded" or "invalid-base" or "credential-unavailable" } => "failed",
            { Phase: < 4 } when retryAt is not null => "rate-limited",
            { FailureCode: not null } => "failed",
            { Phase: 1 or 2 } => "importing",
            { Phase: 3 } => "reconciling",
            { Phase: 4, ResultRevisionId: { } id } when id == active?.RevisionId && !eligible => "active-unavailable",
            { Phase: 4, ResultRevisionId: { } id } when id == active?.RevisionId => "active",
            { Phase: 4 } when !eligible => "candidate-unavailable",
            { Phase: 4 } when import.ResultSourceVersion is null || !import.ResultSourceVersion.AsSpan().SequenceEqual(source.Version) ||
                source.LastSuccessfulCheckAt is null || source.LastSuccessfulCheckAt > now ||
                source.LastSuccessfulCheckAt < now.AddSeconds(-source.StaleAfterSeconds) || import.BaseRevisionId != active?.RevisionId ||
                (active is null ? import.BaseActivationVersion is not null : !active.Version.AsSpan().SequenceEqual(import.BaseActivationVersion)) => "candidate-stale",
            { Phase: 4 } => "awaiting-activation",
            _ => "stopped"
        };
        var result = new AdvisoryImportProgressView(sourceId, import?.Id, state, import?.WindowStart, import?.WindowEnd, import?.StartedAt,
            import?.PageCount ?? 0, import?.RecordCount ?? 0, import?.TotalBytes ?? 0, retryAt, import?.FailureCode,
            import?.ResultRevisionId, "github-reviewed-only; not a point-in-time snapshot; other sources and applicability unsupported",
            import is null ? null : Convert.ToBase64String(import.Version));
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<IntelligenceSourcePage> GetSourcesAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await (from source in db.IntelligenceSources.AsNoTracking()
                          join activation in db.IntelligenceActivations.AsNoTracking() on source.Id equals activation.SourceId into activations
                          from active in activations.DefaultIfEmpty()
                          orderby source.Key
                          select new { Source = source, ActiveId = (Guid?)active.RevisionId, ActiveVersion = active.Version })
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var now = clock.GetUtcNow();
        var items = rows.Take(PageSize).Select(row => new IntelligenceSourceView(row.Source.Id, row.Source.Key, row.Source.Enabled,
            row.Source.UpdateIntervalSeconds, row.Source.StaleAfterSeconds, row.Source.MaximumBytes, row.Source.MaximumRecords,
            Convert.ToBase64String(row.Source.Version), row.Source.LastCheckedAt, row.Source.LastSuccessfulCheckAt, row.Source.LastError,
            row.Source.LastSuccessfulCheckAt is null || row.Source.LastSuccessfulCheckAt > now ||
            row.Source.LastSuccessfulCheckAt < now.AddSeconds(-row.Source.StaleAfterSeconds), row.ActiveId,
            row.ActiveVersion is null ? "" : Convert.ToBase64String(row.ActiveVersion), row.Source.ProtectedCredential is not null,
            Describe(row.Source.Key))).ToArray();
        return new(items, page, rows.Length > PageSize && page < 1000);
    }

    private static IntelligenceSourceDescription? Describe(string key) => key switch
    {
        "cisa-kev" => new("CISA Known Exploited Vulnerabilities", "known-exploited-vulnerabilities",
            "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json",
            "Known exploited CVEs across products; not a complete vulnerability catalog or proof of asset applicability.", KevUpdate.LicenseEvidence),
        "github-reviewed" => new("GitHub Reviewed Advisories", "open-source-package-advisories",
            "https://api.github.com/advisories",
            "GitHub-reviewed package advisories; excludes unreviewed and malware advisories and does not establish asset applicability.",
            AdvisoryRevisionBuilder.LicenseEvidence),
        _ => null
    };

    public async Task<IntelligenceRevisionPage?> GetRevisionsAsync(Guid sourceId, int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.IntelligenceSources.AsNoTracking().AnyAsync(x => x.Id == sourceId, token)) return null;
        var activeId = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == sourceId)
            .Select(x => (Guid?)x.RevisionId).SingleOrDefaultAsync(token);
        var rows = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId)
            .OrderByDescending(x => x.RetrievedAt).ThenByDescending(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.Id, x.UpstreamVersion, x.ReleasedAt, x.RetrievedAt, x.Digest, x.SchemaVersion,
                x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked, x.Normalized }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new IntelligenceRevisionView(x.Id, x.UpstreamVersion, x.ReleasedAt, x.RetrievedAt,
            Convert.ToHexStringLower(x.Digest), x.SchemaVersion, x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked, x.Normalized,
            x.Id == activeId)).ToArray(), page, rows.Length > PageSize && page < 1000);
    }
}
