using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

internal static class KevFreshness
{
    internal static async Task<bool> IsFreshAsync(SecurityAnalysisDbContext db, IntelligenceSource source, Guid revisionId,
        string mapping, DateTimeOffset? releasedAt, DateTimeOffset now, CancellationToken token)
    {
        DateTimeOffset? observed;
        if (mapping == KevUpdate.OfflineMappingVersion)
        {
            var imported = await db.OfflineImports.Where(x => x.SourceId == source.Id && x.RevisionId == revisionId)
                .MaxAsync(x => (DateTimeOffset?)x.ImportedAt, token);
            if (imported is null || releasedAt is null || imported > now || releasedAt > now) return false;
            observed = imported < releasedAt ? imported : releasedAt;
        }
        else observed = source.LastSuccessfulCheckAt;
        return observed is not null && observed <= now && observed >= now.AddSeconds(-source.StaleAfterSeconds);
    }
}
