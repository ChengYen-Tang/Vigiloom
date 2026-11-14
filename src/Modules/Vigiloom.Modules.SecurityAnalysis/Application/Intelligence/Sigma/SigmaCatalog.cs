using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

/// <summary>Version-fenced access to the attributed Sigma source subset.</summary>
public sealed class SigmaCatalog(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock) : ISigmaCatalog
{
    private const int PageSize = 50;
    private const string License = "DRL-1.1";

    public async Task<SigmaSnapshot> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == SigmaRefresh.SourceKey, cancellationToken);
        if (source is null) return Empty(SigmaAvailability.Unavailable);
        if (!source.Enabled) return Empty(SigmaAvailability.Disabled, source.Id);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
        if (active is null) return Empty(SigmaAvailability.NoActiveRevision, source.Id);
        if (!Fresh(source)) return Empty(SigmaAvailability.Stale, source.Id);
        var included = await SigmaRevisionEligibility.IncludedCountAsync(db, source.Id, active.RevisionId, cancellationToken);
        if (included is null) return Empty(SigmaAvailability.Unavailable, source.Id);
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleAsync(x => x.Id == active.RevisionId, cancellationToken);
        var manifest = await db.SigmaManifests.AsNoTracking().SingleAsync(x => x.RevisionId == active.RevisionId, cancellationToken);
        if (!Fresh(source)) return Empty(SigmaAvailability.Stale, source.Id);
        return new(SigmaAvailability.Ready, source.Id, active.RevisionId, Convert.ToBase64String(active.Version),
            revision.UpstreamVersion, Convert.ToHexStringLower(manifest.Digest), included.Value, manifest.ExcludedCount);
    }

    public async Task<SigmaSourcePage> ReadPageAsync(SigmaSnapshot selection, string? afterPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (afterPath is { Length: > 512 }) throw new ArgumentOutOfRangeException(nameof(afterPath));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var status = await CheckSelectionAsync(db, selection, cancellationToken);
        if (status != SigmaAvailability.Ready) return new(status, [], null);
        var query = db.SigmaRules.AsNoTracking().Where(x => x.RevisionId == selection.RevisionId && x.ExclusionReason == null);
        if (afterPath is not null) query = query.Where(x => string.Compare(x.Path, afterPath) > 0);
        var rules = await query.OrderBy(x => x.Path).Take(PageSize + 1)
            .Select(x => new { x.Path, x.Title, x.RuleId, x.Author, x.License, x.Digest }).ToArrayAsync(cancellationToken);
        if (rules.Any(x => SigmaLicense.ExclusionReason(x.Path, x.Author, x.License) is not null))
            return new(SigmaAvailability.Unavailable, [], null);
        var result = rules.Take(PageSize).Select(x => Describe(selection.Commit!, x.Path, x.Title, x.RuleId, x.Author!, x.Digest)).ToArray();
        if (!await StillFreshAsync(db, selection.SourceId!.Value, cancellationToken)) return new(SigmaAvailability.Stale, [], null);
        return new(SigmaAvailability.Ready, result, rules.Length > PageSize ? result[^1].Path : null);
    }

    public async Task<SigmaSourceContent> ReadRuleAsync(SigmaSnapshot selection, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512) throw new ArgumentException("Invalid rule path.", nameof(path));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var status = await CheckSelectionAsync(db, selection, cancellationToken);
        if (status != SigmaAvailability.Ready) return new(status, null, null);
        var rule = await db.SigmaRules.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == selection.RevisionId && x.Path == path && x.ExclusionReason == null, cancellationToken);
        if (rule is null || SigmaLicense.ExclusionReason(rule.Path, rule.Author, rule.License) is not null ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(rule.Content), rule.Digest)) return new(SigmaAvailability.Unavailable, null, null);
        SigmaRuleMetadata metadata;
        try { metadata = SigmaRuleValidator.Validate(rule.Content); }
        catch (InvalidDataException) { return new(SigmaAvailability.Unavailable, null, null); }
        if (metadata.Title != rule.Title || metadata.Id != rule.RuleId || metadata.Author != rule.Author || metadata.License != rule.License ||
            metadata.Status != rule.Status || metadata.Product != rule.Product || metadata.Category != rule.Category || metadata.Service != rule.Service)
            return new(SigmaAvailability.Unavailable, null, null);
        if (!await StillFreshAsync(db, selection.SourceId!.Value, cancellationToken)) return new(SigmaAvailability.Stale, null, null);
        return new(SigmaAvailability.Ready, Describe(selection.Commit!, rule.Path, rule.Title, rule.RuleId, rule.Author!, rule.Digest), rule.Content);
    }

    // Immutable revision rows are checked once when producing the snapshot. Each read fences the selection and
    // policy; rule bytes and their metadata are checked again before a consumer can use them.
    private async Task<SigmaAvailability> CheckSelectionAsync(SecurityAnalysisDbContext db, SigmaSnapshot selection, CancellationToken token)
    {
        if (selection.Availability != SigmaAvailability.Ready || selection.SourceId is not { } sourceId || selection.RevisionId is not { } revisionId ||
            selection.ActivationVersion is null || selection.ManifestSha256 is null || selection.Commit is null)
            return SigmaAvailability.SelectionChanged;
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == SigmaRefresh.SourceKey, token);
        if (source is null) return SigmaAvailability.Unavailable;
        if (!source.Enabled) return SigmaAvailability.Disabled;
        if (!Fresh(source)) return SigmaAvailability.Stale;
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (active is null || active.RevisionId != revisionId || Convert.ToBase64String(active.Version) != selection.ActivationVersion)
            return SigmaAvailability.SelectionChanged;
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Id == revisionId, token);
        var manifest = await db.SigmaManifests.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revisionId, token);
        if (revision is null || revision.Revoked || !revision.Normalized || revision.SchemaVersion != SigmaRefresh.SchemaVersion ||
            revision.MappingVersion != SigmaRefresh.MappingVersion || revision.LicenseEvidence != SigmaRefresh.LicenseEvidence || revision.UpstreamVersion != selection.Commit ||
            manifest is null || manifest.PolicyVersion != SigmaManifestBuilder.PolicyVersion || manifest.IncludedCount != selection.IncludedCount ||
            manifest.ExcludedCount != selection.ExcludedCount || manifest.IncludedCount <= 0 ||
            (long)manifest.IncludedCount + manifest.ExcludedCount != revision.RecordCount || Convert.ToHexStringLower(manifest.Digest) != selection.ManifestSha256 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(revision.Artifact), revision.Digest) ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token)) return SigmaAvailability.Unavailable;
        return SigmaAvailability.Ready;
    }

    private bool Fresh(IntelligenceSource source)
    {
        var now = clock.GetUtcNow();
        return source.LastSuccessfulCheckAt is { } last && last <= now && last >= now.AddSeconds(-source.StaleAfterSeconds);
    }

    private async Task<bool> StillFreshAsync(SecurityAnalysisDbContext db, Guid sourceId, CancellationToken token)
    {
        var source = await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == sourceId, token);
        return Fresh(source);
    }

    private static SigmaSnapshot Empty(SigmaAvailability status, Guid? sourceId = null) => new(status, sourceId, null, null, null, null, 0, 0);
    private static SigmaSourceRule Describe(string commit, string path, string title, string? id, string author, byte[] digest) =>
        new(path, title, id, author, License, "https://github.com/SigmaHQ/Detection-Rule-License/blob/bcbdc605172d00390572cd774ba395ec9e975dfc/LICENSE.Detection.Rules.md", "https://github.com/SigmaHQ/sigma/blob/" + commit + "/" +
            string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), Convert.ToHexStringLower(digest));
}
