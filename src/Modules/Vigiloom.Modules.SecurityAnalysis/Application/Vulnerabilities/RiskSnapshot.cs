using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Resolves the existing risk sources for a locked assessment query.</summary>
internal sealed record RiskSnapshot(Guid? KevRevision, IntelligenceSource? KevSource, KevSnapshot? Kev,
    Guid? EpssRevision, IntelligenceSource? EpssSource, string? EpssActivation, DateTimeOffset? EpssScoredAt)
{
    internal static async Task<RiskSnapshot> LoadAsync(SecurityAnalysisDbContext db,
        IReadOnlyDictionary<Guid, IntelligenceSource> sources, KevSnapshot? kev, DateTimeOffset now, CancellationToken token)
    {
        Guid? kevRevision = null;
        IntelligenceSource? kevSource = null;
        if (kev is { Availability: KevAvailability.Ready, SourceId: { } sourceId, RevisionId: { } revision, ActivationVersion: { } encoded, MappingVersion: { } mapping })
        {
            byte[] version;
            try { version = Convert.FromBase64String(encoded); } catch (FormatException) { version = []; }
            sources.TryGetValue(sourceId, out var candidate);
            var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
            if (version.Length == 8 && candidate is { Enabled: true, Key: "cisa-kev" } && active is not null &&
                active.RevisionId == revision && active.Version.AsSpan().SequenceEqual(version) &&
                await KevFreshness.IsFreshAsync(db, candidate, revision, mapping, kev.ReleasedAt, now, token) &&
                await db.IntelligenceActivationEvents.AsNoTracking().AnyAsync(x => x.SourceId == sourceId && x.RevisionId == revision, token) &&
                await db.IntelligenceRevisions.AsNoTracking().AnyAsync(x => x.SourceId == sourceId && x.Id == revision && x.Normalized && !x.Revoked &&
                    !db.IntelligenceRevisions.Any(other => other.SourceId == sourceId && other.Digest == x.Digest && other.Revoked), token))
            { kevRevision = revision; kevSource = candidate; }
        }
        var epssSource = sources.Values.SingleOrDefault(x => x.Key == EpssSnapshotStage.SourceKey);
        Guid? epssRevision = null;
        string? epssActivation = null;
        DateTimeOffset? scoredAt = null;
        if (epssSource is { Enabled: true, LastSuccessfulCheckAt: { } checkedAt } && checkedAt <= now && checkedAt >= now.AddSeconds(-epssSource.StaleAfterSeconds))
        {
            var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == epssSource.Id)
                .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(token);
            if (active is not null && await EpssRevisionEligibility.IsPublishableAsync(db, epssSource.Id, active.RevisionId, now, epssSource.StaleAfterSeconds, token))
            {
                epssRevision = active.RevisionId;
                epssActivation = Convert.ToBase64String(active.Version);
                scoredAt = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.Id == active.RevisionId).Select(x => x.ReleasedAt).SingleAsync(token);
            }
        }
        return new(kevRevision, kevSource, kev, epssRevision, epssSource, epssActivation, scoredAt);
    }

    internal async Task<bool> IsFreshAsync(SecurityAnalysisDbContext db, TimeProvider clock, CancellationToken token)
    {
        DateTimeOffset? imported = null;
        if (KevRevision is { } revision && KevSource is { } offlineSource && Kev?.MappingVersion == KevUpdate.OfflineMappingVersion)
            imported = await db.OfflineImports.AsNoTracking().Where(x => x.SourceId == offlineSource.Id && x.RevisionId == revision)
                .MaxAsync(x => (DateTimeOffset?)x.ImportedAt, token);
        var now = clock.GetUtcNow();
        if (KevRevision is { } && KevSource is { } sourceValue)
        {
            var source = sourceValue;
            DateTimeOffset? observed = source.LastSuccessfulCheckAt;
            if (Kev?.MappingVersion == KevUpdate.OfflineMappingVersion)
            {
                observed = imported is { } time && Kev.ReleasedAt is { } released && time <= now && released <= now
                    ? (time < released ? time : released) : null;
            }
            if (observed is not { } kevTime || kevTime > now || kevTime < now.AddSeconds(-source.StaleAfterSeconds)) return false;
        }
        return EpssRevision is null || EpssSource is { LastSuccessfulCheckAt: { } last } epss &&
            last <= now && last >= now.AddSeconds(-epss.StaleAfterSeconds) && EpssScoredAt is { } scored &&
            scored <= now && scored >= now.AddSeconds(-epss.StaleAfterSeconds);
    }
}
