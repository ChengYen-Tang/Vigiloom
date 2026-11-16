using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

/// <summary>Exposes the currently published reviewed check to baseline consumers.</summary>
public sealed class BaselineSource(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock) : IBaselineSource
{
    public async Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var custom = await db.IntelligenceSources.AsNoTracking().Where(x => x.Key.StartsWith(BaselineEligibility.CustomKeyPrefix) &&
                db.IntelligenceActivations.Any(active => active.SourceId == x.Id))
            .OrderBy(x => x.RowId).Select(x => x.Key).Take(98).ToArrayAsync(cancellationToken);
        return BaselineDefinitions.All.Select(x => x.Key).Concat(custom).ToArray();
    }

    public async Task<BaselineSourceSnapshot> GetActiveAsync(string sourceKey, CancellationToken cancellationToken)
    {
        var definition = BaselineDefinitions.Find(sourceKey);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == sourceKey, cancellationToken);
        if (source is null) return Empty(BaselineSourceAvailability.Unavailable);
        var custom = definition is null && BaselineEligibility.IsCustomKey(source.Key, source.Id);
        if (definition is null && !custom) return Empty(BaselineSourceAvailability.Unavailable, source.Id);
        if (!source.Enabled) return Empty(BaselineSourceAvailability.Disabled, source.Id);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
        if (active is null) return Empty(BaselineSourceAvailability.NoActiveRevision, source.Id);
        var now = clock.GetUtcNow();
        if (!custom && (source.LastSuccessfulCheckAt is not { } last || last > now || last < now.AddSeconds(-source.StaleAfterSeconds)))
            return Empty(BaselineSourceAvailability.Stale, source.Id);
        if (!await BaselineEligibility.IsEligibleAsync(db, sourceKey, source.Id, active.RevisionId, cancellationToken))
            return Empty(BaselineSourceAvailability.Unavailable, source.Id);
        if (definition is not null)
            return new(BaselineSourceAvailability.Ready, source.Id, active.RevisionId, Convert.ToBase64String(active.Version), definition.Commit, definition.Check);
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleAsync(x => x.Id == active.RevisionId, cancellationToken);
        return new(BaselineSourceAvailability.Ready, source.Id, active.RevisionId, Convert.ToBase64String(active.Version), revision.UpstreamVersion,
            BaselineEligibility.ReadCustom(revision, source.Id));
    }

    private static BaselineSourceSnapshot Empty(BaselineSourceAvailability availability, Guid? sourceId = null) =>
        new(availability, sourceId, null, null, null, null);
}
