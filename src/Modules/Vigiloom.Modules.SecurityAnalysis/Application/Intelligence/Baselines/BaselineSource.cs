using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

/// <summary>Exposes the currently published reviewed check to baseline consumers.</summary>
public sealed class BaselineSource(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock) : IBaselineSource
{
    public async Task<BaselineSourceSnapshot> GetActiveAsync(string sourceKey, CancellationToken cancellationToken)
    {
        var definition = BaselineDefinitions.Find(sourceKey);
        if (definition is null) return Empty(BaselineSourceAvailability.Unavailable);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == sourceKey, cancellationToken);
        if (source is null) return Empty(BaselineSourceAvailability.Unavailable);
        if (!source.Enabled) return Empty(BaselineSourceAvailability.Disabled, source.Id);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
        if (active is null) return Empty(BaselineSourceAvailability.NoActiveRevision, source.Id);
        var now = clock.GetUtcNow();
        if (source.LastSuccessfulCheckAt is not { } last || last > now || last < now.AddSeconds(-source.StaleAfterSeconds))
            return Empty(BaselineSourceAvailability.Stale, source.Id);
        if (!await BaselineEligibility.IsEligibleAsync(db, sourceKey, source.Id, active.RevisionId, cancellationToken))
            return Empty(BaselineSourceAvailability.Unavailable, source.Id);
        return new(BaselineSourceAvailability.Ready, source.Id, active.RevisionId, Convert.ToBase64String(active.Version), definition.Commit, definition.Check);
    }

    private static BaselineSourceSnapshot Empty(BaselineSourceAvailability availability, Guid? sourceId = null) =>
        new(availability, sourceId, null, null, null, null);
}
