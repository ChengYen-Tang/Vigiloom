using System.Data;
using System.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
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
                          select new { Source = source, ActiveId = (Guid?)active.RevisionId, ActiveVersion = active.Version,
                              ActiveUnavailable = active != null && !db.IntelligenceRevisions.Any(revision => revision.SourceId == source.Id && revision.Id == active.RevisionId &&
                                  !revision.Revoked && revision.Normalized && !db.IntelligenceRevisions.Any(other => other.SourceId == source.Id && other.Digest == revision.Digest && other.Revoked)) })
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var now = clock.GetUtcNow();
        var advisory = rows.Take(PageSize).FirstOrDefault(x => x.Source.Key == "github-reviewed" && x.ActiveId != null && !x.ActiveUnavailable);
        var advisoryUnavailable = advisory is not null && !await AdvisoryRevisionEligibility.IsPublishableAsync(db, advisory.Source.Id, advisory.ActiveId!.Value, token);
        var kev = rows.Take(PageSize).FirstOrDefault(x => x.Source.Key == "cisa-kev" && x.ActiveId != null && !x.ActiveUnavailable);
        bool? kevFresh = null;
        if (kev is not null)
        {
            var activeRevision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == kev.Source.Id && x.Id == kev.ActiveId)
                .Select(x => new { x.Id, x.MappingVersion, x.ReleasedAt }).SingleOrDefaultAsync(token);
            if (activeRevision is not null)
                kevFresh = await KevFreshness.IsFreshAsync(db, kev.Source, activeRevision.Id, activeRevision.MappingVersion, activeRevision.ReleasedAt, now, token);
        }
        var items = rows.Take(PageSize).Select(row => new IntelligenceSourceView(row.Source.Id, row.Source.Key, row.Source.Enabled,
            row.Source.UpdateIntervalSeconds, row.Source.StaleAfterSeconds, row.Source.MaximumBytes, row.Source.MaximumRecords,
            Convert.ToBase64String(row.Source.Version), row.Source.LastCheckedAt, row.Source.LastSuccessfulCheckAt, row.Source.LastError,
            row.Source.Key == "cisa-kev" && kevFresh is { } fresh ? !fresh : row.Source.LastSuccessfulCheckAt is null || row.Source.LastSuccessfulCheckAt > now ||
            row.Source.LastSuccessfulCheckAt < now.AddSeconds(-row.Source.StaleAfterSeconds), row.ActiveId,
            row.ActiveVersion is null ? "" : Convert.ToBase64String(row.ActiveVersion), row.Source.ProtectedCredential is not null,
            Describe(row.Source.Key), row.ActiveUnavailable || row.Source.Key == "github-reviewed" && advisoryUnavailable)).ToArray();
        return new(items, page, rows.Length > PageSize && page < 1000);
    }

    private static IntelligenceSourceDescription? Describe(string key) => key switch
    {
        "cisa-kev" => new("CISA Known Exploited Vulnerabilities", "known-exploited-vulnerabilities",
            "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json",
            "Known exploited CVEs across products; not a complete vulnerability catalog or proof of asset applicability.", KevUpdate.LicenseEvidence,
            "Multiple platforms and products; applicability depends on the affected product and version."),
        "github-reviewed" => new("GitHub Reviewed Advisories", "open-source-package-advisories",
            "https://api.github.com/advisories",
            "GitHub-reviewed package advisories; excludes unreviewed and malware advisories and does not establish asset applicability.",
            AdvisoryRevisionBuilder.LicenseEvidence, "Open-source package ecosystems reported by each advisory; asset applicability is not determined here."),
        "sigmahq-rules" => new("SigmaHQ 規則", "behavioral-rules", "https://github.com/SigmaHQ/sigma",
            "Source rules are retained for review; detection and publication remain unsupported until the consumer is verified.", SigmaRefresh.LicenseEvidence,
            "Platform and event-source requirements vary by rule; compatible collection and detection are not yet verified."),
        "signature-base" => new("Signature-Base", "IOC / YARA", "https://github.com/Neo23x0/signature-base",
            "IOC and YARA source files are retained for review; interpretation, detection and publication remain unsupported.", SignatureRefresh.LicenseEvidence,
            "Platform and dependency requirements vary by file and rule; consumer compatibility has not been verified."),
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
                x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked, x.Normalized,
                OfflineImportAt = db.OfflineImports.Where(i => i.SourceId == sourceId && i.RevisionId == x.Id).Max(i => (DateTimeOffset?)i.ImportedAt) }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new IntelligenceRevisionView(x.Id, x.UpstreamVersion, x.ReleasedAt, x.RetrievedAt,
            Convert.ToHexStringLower(x.Digest), x.SchemaVersion, x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked, x.Normalized,
            x.Id == activeId, x.OfflineImportAt)).ToArray(), page, rows.Length > PageSize && page < 1000);
    }

    public async Task<SigmaRulePage?> GetSigmaRulesAsync(Guid sourceId, Guid revisionId, int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var exists = await (from source in db.IntelligenceSources.AsNoTracking()
                            join revision in db.IntelligenceRevisions.AsNoTracking() on source.Id equals revision.SourceId
                            where source.Id == sourceId && source.Key == "sigmahq-rules" && revision.Id == revisionId &&
                                  revision.MappingVersion == SigmaRefresh.MappingVersion && revision.SchemaVersion == SigmaRefresh.SchemaVersion &&
                                  revision.LicenseEvidence == SigmaRefresh.LicenseEvidence && revision.Normalized
                            select revision.Id).AnyAsync(token);
        if (!exists) return null;
        var rows = await db.SigmaRules.AsNoTracking().Where(x => x.RevisionId == revisionId).OrderBy(x => x.Path)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.Path, x.RuleId, x.Title, x.Author, x.Status, x.License, x.Product, x.Category, x.Service, x.Digest }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new SigmaRuleView(x.Path, x.RuleId, x.Title, x.Author, x.Status, x.License, x.Product, x.Category, x.Service,
            Convert.ToHexStringLower(x.Digest))).ToArray(), page, rows.Length > PageSize && page < 1000);
    }

    public async Task<SignatureFilePage?> GetSignatureFilesAsync(Guid sourceId, Guid revisionId, int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var exists = await (from source in db.IntelligenceSources.AsNoTracking()
                            join revision in db.IntelligenceRevisions.AsNoTracking() on source.Id equals revision.SourceId
                            where source.Id == sourceId && source.Key == "signature-base" && revision.Id == revisionId &&
                                  revision.MappingVersion == SignatureRefresh.MappingVersion && revision.SchemaVersion == SignatureRefresh.SchemaVersion &&
                                  revision.LicenseEvidence == SignatureRefresh.LicenseEvidence && revision.Normalized
                            select revision.Id).AnyAsync(token);
        if (!exists) return null;
        var rows = await db.SignatureFiles.AsNoTracking().Where(x => x.RevisionId == revisionId).OrderBy(x => x.Path)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.Path, x.Kind, x.Digest,
                Entries = x.Kind == 0 ? (int?)db.IocEntries.Count(i => i.SignatureFileRowId == x.RowId) : null,
                Unsupported = x.Kind == 0 ? (int?)db.IocEntries.Count(i => i.SignatureFileRowId == x.RowId && i.Kind == "unsupported") : null }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new SignatureFileView(x.Path, x.Kind switch
        { 0 => "ioc", 1 => "yara", 2 => "license-evidence", _ => "unknown" }, Convert.ToHexStringLower(x.Digest), x.Entries, x.Unsupported)).ToArray(),
            page, rows.Length > PageSize && page < 1000);
    }

    public async Task<IocIssuePage?> GetIocIssuesAsync(Guid sourceId, Guid revisionId, string path, int page, CancellationToken token)
    {
        if (page is < 1 or > 2000 || path.Length is < 1 or > 512) throw new ArgumentOutOfRangeException();
        await using var db = await contexts.CreateDbContextAsync(token);
        var fileId = await (from source in db.IntelligenceSources.AsNoTracking()
                            join revision in db.IntelligenceRevisions.AsNoTracking() on source.Id equals revision.SourceId
                            join file in db.SignatureFiles.AsNoTracking() on revision.Id equals file.RevisionId
                            where source.Id == sourceId && source.Key == "signature-base" && revision.Id == revisionId &&
                                  revision.MappingVersion == SignatureRefresh.MappingVersion && revision.SchemaVersion == SignatureRefresh.SchemaVersion &&
                                  revision.LicenseEvidence == SignatureRefresh.LicenseEvidence && revision.Normalized && file.Kind == 0 && file.Path == path
                            select (long?)file.RowId).SingleOrDefaultAsync(token);
        if (fileId is null) return null;
        var rows = await db.IocEntries.AsNoTracking().Where(x => x.SignatureFileRowId == fileId && x.Kind == "unsupported")
            .OrderBy(x => x.LineNumber).Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.LineNumber, x.Reason }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new IocIssueView(x.LineNumber, x.Reason ?? "unknown")).ToArray(),
            page, rows.Length > PageSize && page < 2000);
    }

    public async Task<SignatureEvidenceView?> GetSignatureEvidenceAsync(Guid sourceId, Guid revisionId, string path, CancellationToken token, int? contextLine = null, int page = 1)
    {
        if (path.Length is < 1 or > 512 || contextLine is < 1 or > 1048576 || page is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(path));
        await using var db = await contexts.CreateDbContextAsync(token);
        var file = await (from source in db.IntelligenceSources.AsNoTracking()
                          join revision in db.IntelligenceRevisions.AsNoTracking() on source.Id equals revision.SourceId
                          join candidate in db.SignatureFiles.AsNoTracking() on revision.Id equals candidate.RevisionId
                          where source.Id == sourceId && source.Key == "signature-base" && revision.Id == revisionId &&
                                revision.MappingVersion == SignatureRefresh.MappingVersion && revision.SchemaVersion == SignatureRefresh.SchemaVersion &&
                                revision.LicenseEvidence == SignatureRefresh.LicenseEvidence && revision.Normalized &&
                                candidate.Kind != 0 && candidate.Path == path
                          select new { candidate.Path, candidate.Digest, candidate.Content }).SingleOrDefaultAsync(token);
        if (file is null) return null;
        if (!SHA256.HashData(file.Content).AsSpan().SequenceEqual(file.Digest))
            return new(file.Path, Convert.ToHexStringLower(file.Digest), [], false, false, false);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(file.Content); }
        catch (DecoderFallbackException) { return new(file.Path, Convert.ToHexStringLower(file.Digest), [], false, true, true); }
        using var reader = new StringReader(text);
        var lines = new List<SignatureEvidenceLine>();
        var number = 0;
        var matches = 0;
        var truncated = false;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (!line.Contains("license", StringComparison.OrdinalIgnoreCase) && !line.Contains("licence", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("author", StringComparison.OrdinalIgnoreCase) && !line.Contains("copyright", StringComparison.OrdinalIgnoreCase)) continue;
            if (++matches <= (page - 1) * 100) continue;
            if (lines.Count == 100) { truncated = true; break; }
            lines.Add(new(number, line.Length > 512 ? line[..512] : line, line.Length > 512));
        }
        List<SignatureEvidenceLine>? surrounding = null;
        var contextHasMore = false;
        if (contextLine is { } center)
        {
            surrounding = [];
            using var contextReader = new StringReader(text);
            var current = 0;
            while (contextReader.ReadLine() is { } line && ++current <= center + 10)
                if (current >= Math.Max(1, center - 10)) surrounding.Add(new(current, line.Length > 512 ? line[..512] : line, line.Length > 512));
            contextHasMore = current > center + 10;
        }
        return new(file.Path, Convert.ToHexStringLower(file.Digest), lines, truncated, false, true, contextLine, surrounding, page, contextHasMore);
    }
}
