using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Serves bounded lookups against a fixed, previously activated KEV revision.</summary>
public sealed class KevCatalog(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock) : IKevCatalog
{
    private const int MaximumLookupIds = 500;

    public async Task<KevSnapshot> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == "cisa-kev", cancellationToken);
        if (source is null) return new(KevAvailability.Unavailable, null, null, null, null, null, null);
        if (!source.Enabled) return new(KevAvailability.Disabled, source.Id, null, null, null, null, source.LastSuccessfulCheckAt);
        var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == source.Id)
            .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(cancellationToken);
        if (active is null) return new(KevAvailability.NoActiveRevision, source.Id, null, null, null, null, source.LastSuccessfulCheckAt);
        var revision = await HeaderAsync(db, source.Id, active.RevisionId, cancellationToken);
        if (revision is null || !await EligibleAsync(db, source.Id, revision, cancellationToken))
            return new(KevAvailability.Unavailable, source.Id, active.RevisionId, null, null, null, source.LastSuccessfulCheckAt);
        return new(Freshness(source), source.Id, revision.Id, Convert.ToBase64String(active.Version), revision.UpstreamVersion, revision.ReleasedAt, source.LastSuccessfulCheckAt);
    }

    public Task<KevLookupResult> FindCurrentAsync(Guid sourceId, Guid revisionId, string activationVersion,
        IReadOnlyCollection<string> cveIds, CancellationToken cancellationToken)
    {
        byte[] version;
        try { version = activationVersion is { Length: <= 32 } ? Convert.FromBase64String(activationVersion) : []; }
        catch (FormatException) { version = []; }
        return FindAsync(sourceId, revisionId, cveIds, false, version, cancellationToken);
    }

    public Task<KevLookupResult> FindHistoricalAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> cveIds,
        CancellationToken cancellationToken) => FindAsync(sourceId, revisionId, cveIds, true, [], cancellationToken);

    private async Task<KevLookupResult> FindAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> cveIds,
        bool historical, byte[] version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cveIds);
        if (sourceId == Guid.Empty || revisionId == Guid.Empty || cveIds.Count > MaximumLookupIds || cveIds.Any(x => !ValidCve(x)))
            throw new ArgumentException("Use valid identities and at most 500 canonical CVE identifiers.", nameof(cveIds));
        var ids = cveIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == "cisa-kev", cancellationToken);
        if (source is null) return new(KevAvailability.Unavailable, sourceId, revisionId, historical, []);
        if (!source.Enabled) return new(KevAvailability.Disabled, sourceId, revisionId, historical, []);
        var availability = Freshness(source);
        if (!historical)
        {
            var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, cancellationToken);
            if (active is null || active.RevisionId != revisionId || version.Length != 8 || !active.Version.AsSpan().SequenceEqual(version))
                return new(KevAvailability.SelectionChanged, sourceId, revisionId, false, []);
            if (availability != KevAvailability.Ready) return new(availability, sourceId, revisionId, false, []);
        }
        var revision = await HeaderAsync(db, sourceId, revisionId, cancellationToken);
        if (revision is null || !await EligibleAsync(db, sourceId, revision, cancellationToken) ||
            !await db.IntelligenceActivationEvents.AsNoTracking().AnyAsync(x => x.SourceId == sourceId && x.RevisionId == revisionId, cancellationToken))
            return new(KevAvailability.Unavailable, sourceId, revisionId, historical, []);
        var rows = await db.KevRecords.AsNoTracking().Where(x => x.RevisionId == revisionId && ids.Contains(x.CveId))
            .OrderBy(x => x.CveId).Select(x => new { x.CveId, x.VendorProject, x.Product, x.VulnerabilityName,
                x.DateAdded, x.Description, x.RequiredAction, x.DueDate, x.RansomwareUse, x.ForensicTriage, x.Notes, x.CwesJson })
            .ToArrayAsync(cancellationToken);
        availability = Freshness(source);
        if (!historical && availability != KevAvailability.Ready) return new(availability, sourceId, revisionId, false, []);
        var entries = rows.Select(x => new KevEntry(x.CveId, x.VendorProject, x.Product, x.VulnerabilityName,
            x.DateAdded, x.Description, x.RequiredAction, x.DueDate, x.RansomwareUse, x.ForensicTriage, x.Notes,
            x.CwesJson is null ? null : JsonSerializer.Deserialize<string[]>(x.CwesJson))).ToArray();
        return new(availability, sourceId, revisionId, historical, entries);
    }

    private KevAvailability Freshness(IntelligenceSource source)
    {
        var now = clock.GetUtcNow();
        return source.LastSuccessfulCheckAt is null || source.LastSuccessfulCheckAt > now ||
            source.LastSuccessfulCheckAt < now.AddSeconds(-source.StaleAfterSeconds) ? KevAvailability.Stale : KevAvailability.Ready;
    }

    private static Task<RevisionHeader?> HeaderAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId, CancellationToken token) =>
        db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.Id == revisionId)
            .Select(x => new RevisionHeader(x.Id, x.UpstreamVersion, x.ReleasedAt, x.Digest, x.Revoked, x.Normalized,
                x.SchemaVersion, x.MappingVersion, x.LicenseEvidence)).SingleOrDefaultAsync(token);

    private static async Task<bool> EligibleAsync(SecurityAnalysisDbContext db, Guid sourceId, RevisionHeader revision, CancellationToken token) =>
        !revision.Revoked && revision.Normalized && revision.SchemaVersion == KevCatalogValidator.SchemaRevision &&
        revision.MappingVersion == KevUpdate.MappingVersion && revision.LicenseEvidence == KevUpdate.LicenseEvidence &&
        !await db.IntelligenceRevisions.AsNoTracking().AnyAsync(x => x.SourceId == sourceId && x.Digest == revision.Digest && x.Revoked, token);

    private static bool ValidCve(string? id)
    {
        if (id is null || id.Length is < 13 or > 28 || !id.StartsWith("CVE-", StringComparison.Ordinal) || id[8] != '-') return false;
        for (var i = 4; i < id.Length; i++)
            if (i != 8 && !char.IsAsciiDigit(id[i])) return false;
        return true;
    }

    private sealed record RevisionHeader(Guid Id, string UpstreamVersion, DateTimeOffset? ReleasedAt, byte[] Digest,
        bool Revoked, bool Normalized, string SchemaVersion, string MappingVersion, string LicenseEvidence);
}
