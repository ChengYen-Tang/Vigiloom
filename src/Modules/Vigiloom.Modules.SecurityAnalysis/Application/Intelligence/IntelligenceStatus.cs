using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Bounded non-secret intelligence status and retained revision metadata.</summary>
public sealed class IntelligenceStatus(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 50;

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
            row.ActiveVersion is null ? "" : Convert.ToBase64String(row.ActiveVersion))).ToArray();
        return new(items, page, rows.Length > PageSize && page < 1000);
    }

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
                x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new IntelligenceRevisionView(x.Id, x.UpstreamVersion, x.ReleasedAt, x.RetrievedAt,
            Convert.ToHexStringLower(x.Digest), x.SchemaVersion, x.MappingVersion, x.LicenseEvidence, x.RecordCount, x.Revoked,
            x.Id == activeId)).ToArray(), page, rows.Length > PageSize && page < 1000);
    }
}
