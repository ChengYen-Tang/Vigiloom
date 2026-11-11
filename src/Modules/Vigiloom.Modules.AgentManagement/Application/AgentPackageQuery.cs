using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class AgentPackageQuery(IDbContextFactory<AgentManagementDbContext> contexts)
{
    private const int PageSize = 50;

    public async Task<AgentPackagePage> GetAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.Packages.AsNoTracking().OrderByDescending(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new AgentPackageView(x.Id, x.Version, x.RuntimeIdentifier, x.Size,
                x.Sha256, x.Listed, x.Revoked, x.VerifiedUntil)).ToArrayAsync(cancellationToken);
        return new AgentPackagePage(rows.Take(PageSize).ToArray(), page, rows.Length > PageSize);
    }
}
