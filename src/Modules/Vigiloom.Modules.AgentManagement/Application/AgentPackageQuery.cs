using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class AgentPackageQuery(IDbContextFactory<AgentManagementDbContext> contexts, IPackageLibraryTransport transport)
{
    private const int PageSize = 50;

    public async Task<AgentPackagePage> GetAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.Packages.AsNoTracking().OrderByDescending(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new PackageRow(x.Id, x.Version, x.RuntimeIdentifier, x.Size, x.Sha256, x.ManifestSha256, x.Listed, x.Revoked, x.VerifiedUntil))
            .ToArrayAsync(cancellationToken);
        var items = rows.Take(PageSize).Select(View).ToArray();
        return new AgentPackagePage(items, page, rows.Length > PageSize);
    }

    public async Task<IReadOnlyList<AgentPackageView>> GetVersionsAsync(string[] versions, CancellationToken cancellationToken)
    {
        if (versions.Length == 0) return [];
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.Packages.AsNoTracking().Where(x => versions.Contains(x.Version))
            .Select(x => new PackageRow(x.Id, x.Version, x.RuntimeIdentifier, x.Size, x.Sha256, x.ManifestSha256, x.Listed, x.Revoked, x.VerifiedUntil))
            .ToArrayAsync(cancellationToken);
        return rows.Select(View).ToArray();
    }

    private AgentPackageView View(PackageRow x) => new(x.Id, x.Version, x.RuntimeIdentifier, x.Size,
        x.Sha256, x.Listed, x.Revoked, x.VerifiedUntil) { Cached = transport.IsCached(x.Sha256, x.ManifestSha256) };

    private sealed record PackageRow(Guid Id, string Version, string RuntimeIdentifier, long Size, string Sha256,
        string ManifestSha256, bool Listed, bool Revoked, DateTimeOffset VerifiedUntil);
}
