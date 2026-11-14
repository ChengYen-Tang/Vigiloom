using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

/// <summary>Central, version-aware lookup of retained GitHub-reviewed advisory records.</summary>
public sealed class AdvisoryCatalog(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock) : IAdvisoryCatalog
{
    private const int MaximumLookupIds = 20;

    public async Task<AdvisorySnapshot> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == "github-reviewed", cancellationToken);
        if (source is null) return new(AdvisoryAvailability.Unavailable, null, null, null, null, null);
        if (!source.Enabled) return new(AdvisoryAvailability.Disabled, source.Id, null, null, null, source.LastSuccessfulCheckAt);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
        if (active is null) return new(AdvisoryAvailability.NoActiveRevision, source.Id, null, null, null, source.LastSuccessfulCheckAt);
        var revision = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == source.Id && x.Id == active.RevisionId)
            .Select(x => new { x.UpstreamVersion }).SingleOrDefaultAsync(cancellationToken);
        if (revision is null || !await AdvisoryRevisionEligibility.IsPublishableAsync(db, source.Id, active.RevisionId, cancellationToken))
            return new(AdvisoryAvailability.Unavailable, source.Id, active.RevisionId, null, null, source.LastSuccessfulCheckAt);
        return new(Freshness(source), source.Id, active.RevisionId, Convert.ToBase64String(active.Version), revision.UpstreamVersion, source.LastSuccessfulCheckAt);
    }

    public Task<AdvisoryLookupResult> FindCurrentAsync(Guid sourceId, Guid revisionId, string activationVersion,
        IReadOnlyCollection<string> advisoryIds, CancellationToken cancellationToken)
    {
        byte[] version;
        try { version = activationVersion is { Length: <= 32 } ? Convert.FromBase64String(activationVersion) : []; }
        catch (FormatException) { version = []; }
        return FindAsync(sourceId, revisionId, advisoryIds, false, version, cancellationToken);
    }

    public Task<AdvisoryLookupResult> FindHistoricalAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> advisoryIds,
        CancellationToken cancellationToken) => FindAsync(sourceId, revisionId, advisoryIds, true, [], cancellationToken);

    private async Task<AdvisoryLookupResult> FindAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> advisoryIds,
        bool historical, byte[] version, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(advisoryIds);
        if (sourceId == Guid.Empty || revisionId == Guid.Empty || advisoryIds.Count > MaximumLookupIds ||
            advisoryIds.Any(id => id is null || !AdvisoryPageValidator.ValidId(id)))
            throw new ArgumentException("Use valid identities and at most 20 canonical GHSA identifiers.", nameof(advisoryIds));
        var ids = advisoryIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == "github-reviewed", token);
        if (source is null) return Result(AdvisoryAvailability.Unavailable);
        if (!source.Enabled) return Result(AdvisoryAvailability.Disabled);
        if (!historical)
        {
            var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
            if (active is null || active.RevisionId != revisionId || version.Length != 8 || !active.Version.AsSpan().SequenceEqual(version))
                return Result(AdvisoryAvailability.SelectionChanged);
            if (Freshness(source) != AdvisoryAvailability.Ready) return Result(AdvisoryAvailability.Stale);
        }
        if (!await db.IntelligenceActivationEvents.AsNoTracking().AnyAsync(x => x.SourceId == sourceId && x.RevisionId == revisionId, token) ||
            !(historical ? await AdvisoryRevisionEligibility.IsEligibleAsync(db, sourceId, revisionId, token) :
                await AdvisoryRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token)))
            return Result(AdvisoryAvailability.Unavailable);
        var entries = new List<AdvisoryEntryView>(ids.Length);
        var rows = from member in db.AdvisoryRevisionEntries.AsNoTracking()
                   join entry in db.AdvisoryImportEntries.AsNoTracking() on member.EntryRowId equals entry.RowId
                   join page in db.AdvisoryImportPages.AsNoTracking() on entry.PageRowId equals page.RowId
                   where member.RevisionId == revisionId && ids.Contains(member.AdvisoryId)
                   orderby member.AdvisoryId
                   select new { member.AdvisoryId, entry.Ordinal, entry.Digest, page.Payload };
        try
        {
            await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(token))
            {
                var parsed = AdvisoryPageValidator.Validate(row.Payload);
                if ((uint)row.Ordinal >= (uint)parsed.Count) return Result(AdvisoryAvailability.Unavailable);
                var record = parsed[row.Ordinal];
                if (record.Id != row.AdvisoryId || !SHA256.HashData(Encoding.UTF8.GetBytes(record.OriginalJson)).AsSpan().SequenceEqual(row.Digest))
                    return Result(AdvisoryAvailability.Unavailable);
                entries.Add(new(record.Id, record.CveId, record.PublishedAt, record.UpdatedAt, record.WithdrawnAt,
                    record.Packages.Select(x => new AdvisoryPackageView(x.Ecosystem, x.Name, x.VulnerableRange, x.FirstPatchedVersion)).ToArray()));
            }
        }
        catch (InvalidDataException) { return Result(AdvisoryAvailability.Unavailable); }
        var availability = Freshness(source);
        if (!historical && availability != AdvisoryAvailability.Ready) return Result(availability);
        return new(availability, sourceId, revisionId, historical, entries);

        AdvisoryLookupResult Result(AdvisoryAvailability availability) => new(availability, sourceId, revisionId, historical, []);
    }

    private AdvisoryAvailability Freshness(IntelligenceSource source)
    {
        var now = clock.GetUtcNow();
        return source.LastSuccessfulCheckAt is null || source.LastSuccessfulCheckAt > now ||
            source.LastSuccessfulCheckAt < now.AddSeconds(-source.StaleAfterSeconds) ? AdvisoryAvailability.Stale : AdvisoryAvailability.Ready;
    }
}
