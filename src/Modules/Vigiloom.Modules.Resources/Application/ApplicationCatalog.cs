using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Lists the resource-owned application identities for administration.</summary>
public sealed class ApplicationCatalog(IDbContextFactory<ResourcesDbContext> contexts, IApplicationSourceVerifier sources)
{
    public async Task<IReadOnlyList<ManagedServiceSummary>> ListAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.ApplicationServices.AsNoTracking().OrderBy(s => s.RowId).Skip((page - 1) * 50).Take(50).ToArrayAsync(token);
        var sourceIds = rows.Select(s => s.SourceId).Distinct().ToArray();
        var statuses = await Task.WhenAll(sourceIds.Select(id => sources.GetAsync(id, token)));
        var names = sourceIds.Zip(statuses).ToDictionary(x => x.First, x => x.Second.Name);
        return rows.Select(s => Map(s, names.GetValueOrDefault(s.SourceId))).ToArray();
    }

    public async Task<ManagedServiceSummary?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var service = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, token);
        return service is null ? null : Map(service, (await sources.GetAsync(service.SourceId, token)).Name);
    }

    private static ManagedServiceSummary Map(ApplicationService service, string? sourceName) => new(service.Id, service.DisplayName,
        service.Environment, service.SourceId, service.RevokedAt, Convert.ToBase64String(service.Version), sourceName, service.ArchivedAt);
}
