using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

internal sealed class TemplateReader(IDbContextFactory<PublishingDbContext> publishing, IDbContextFactory<ResourcesDbContext> resources)
{
    internal const int PageSize = 50;
    internal const int MaximumPage = int.MaxValue / PageSize;

    internal async Task<PublicationTemplateItem?> GetAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await publishing.CreateDbContextAsync(token);
        var row = await db.Templates.AsNoTracking().Where(x => x.Id == id && !x.IsRetired)
            .Select(x => new { x.Id, x.Name, x.ShowLatency, x.ShowObservedAt, x.IsDefault, x.Version }).SingleOrDefaultAsync(token);
        return row is null ? null : new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault, Convert.ToBase64String(row.Version));
    }

    internal async Task<PublicationTemplateItem?> DefaultAsync(CancellationToken token)
    {
        await using var db = await publishing.CreateDbContextAsync(token);
        var row = await db.Templates.AsNoTracking().Where(x => x.IsDefault && !x.IsRetired)
            .Select(x => new { x.Id, x.Name, x.ShowLatency, x.ShowObservedAt, x.IsDefault, x.Version }).SingleOrDefaultAsync(token);
        return row is null ? null : new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault, Convert.ToBase64String(row.Version));
    }

    internal async Task<PublicationTemplatePage> ListAsync(int page, CancellationToken token)
    {
        await using var db = await publishing.CreateDbContextAsync(token);
        var rows = await db.Templates.AsNoTracking().Where(x => !x.IsRetired).OrderBy(x => x.Name).ThenBy(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.Id, x.Name, x.ShowLatency, x.ShowObservedAt, x.IsDefault, x.Version }).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new PublicationTemplateItem(x.Id, x.Name, x.ShowLatency, x.ShowObservedAt,
            x.IsDefault, Convert.ToBase64String(x.Version))).ToArray(), page, page < MaximumPage && rows.Length > PageSize);
    }

    internal async Task<(PublicationImpact? Impact, bool Changed, bool TooMany)> ImpactAsync(Guid id, int page, CancellationToken token)
    {
        if (id.Version != 7) return (null, false, false);
        await using var templates = await publishing.CreateDbContextAsync(token);
        var version = await templates.Templates.AsNoTracking().Where(x => x.Id == id && !x.IsRetired).Select(x => x.Version).SingleOrDefaultAsync(token);
        if (version is null) return (null, false, false);
        await using var db = await resources.CreateDbContextAsync(token);
        var impact = await TemplateImpact.ReadAsync(db, id, version, page, token);
        if (impact is null) return (null, false, true);
        var latest = await templates.Templates.AsNoTracking().Where(x => x.Id == id && !x.IsRetired).Select(x => x.Version).SingleOrDefaultAsync(token);
        if (latest is null || !version.AsSpan().SequenceEqual(latest)) return (null, true, false);
        return (new(impact.Count, impact.PublicCount, impact.Page, impact.Items.Select(x => new PublicationImpactItem(x.Id, x.Name, x.Public)).ToArray(),
            impact.Fingerprint, Convert.ToBase64String(version)), false, false);
    }
}
