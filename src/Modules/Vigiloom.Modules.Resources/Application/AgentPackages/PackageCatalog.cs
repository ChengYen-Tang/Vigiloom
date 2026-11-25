using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application.AgentPackages;

/// <summary>Maintains the resource service's offline Agent package catalogue.</summary>
public sealed class PackageCatalog(IDbContextFactory<ResourcesDbContext> contexts, GitHubAgentReleases releases,
    IPackageLibraryTransport files, IPackageRuntimeUsage runtime)
{
    public async Task<bool> DownloadAsync(string version, string runtimeIdentifier, CancellationToken token)
    {
        var asset = await releases.GetAssetAsync(version, runtimeIdentifier, token);
        if (asset is null) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        var package = await db.AgentPackages.SingleOrDefaultAsync(p => p.Version == version && p.RuntimeIdentifier == runtimeIdentifier, token);
        if (package is not null && (package.Sha256 != asset.Sha256 || package.Size != asset.Size))
            throw new InvalidOperationException("The release asset changed for an existing version.");

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version, rid = runtimeIdentifier, size = asset.Size, sha256 = asset.Sha256 });
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
        if (package is not null && package.ManifestSha256 != manifestHash)
        {
            var existingManifest = await files.ReadManifestAsync(package.ManifestSha256, token);
            if (existingManifest is not null)
            {
                manifest = existingManifest;
                manifestHash = package.ManifestSha256;
            }
            else
            {
                if (await db.AgentVersionAssignments.AnyAsync(a => a.PackageId == package.Id, token))
                    throw new InvalidOperationException("The existing package manifest is unavailable while assigned.");
                package.ManifestSha256 = manifestHash;
            }
        }
        await files.SaveAsync(version, asset, manifestHash, manifest, releases, token);
        if (package is null)
        {
            db.AgentPackages.Add(new AgentPackage { Id = Guid.CreateVersion7(), Version = version, RuntimeIdentifier = runtimeIdentifier,
                Sha256 = asset.Sha256, ManifestSha256 = manifestHash, Size = asset.Size });
        }
        await db.SaveChangesAsync(token);
        return true;
    }

    public async Task<AgentPackagePage> ListAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(page));
        const int pageSize = 50;
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.AgentPackages.AsNoTracking().OrderByDescending(p => p.RowId)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(p => new { p.Id, p.Version, p.RuntimeIdentifier, p.Size, p.Sha256, p.ManifestSha256 }).ToArrayAsync(token);
        var items = rows.Take(pageSize).Select(p => new AgentPackageView(p.Id, p.Version, p.RuntimeIdentifier, p.Size, p.Sha256)
            { Cached = files.IsCached(p.Sha256, p.ManifestSha256) }).ToArray();
        return new AgentPackagePage(items, page, rows.Length > pageSize);
    }

    public async Task<AgentPackageRemovalResult> RemoveAsync(Guid packageId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var candidate = await db.AgentPackages.AsNoTracking().Where(p => p.Id == packageId).Select(p => new { p.Sha256 }).SingleOrDefaultAsync(token);
        if (candidate is null) return AgentPackageRemovalResult.NotFound;
        var related = await db.AgentPackages.FromSqlInterpolated($"SELECT * FROM [resources].[AgentPackages] WITH (UPDLOCK, HOLDLOCK) WHERE [Sha256] = {candidate.Sha256}").ToArrayAsync(token);
        var selected = related.SingleOrDefault(p => p.Id == packageId);
        if (selected is null) return AgentPackageRemovalResult.NotFound;
        if (await db.AgentVersionAssignments.AnyAsync(a => a.PackageId == packageId, token) ||
            await runtime.IsInstalledAsync(selected.Version, selected.RuntimeIdentifier, token)) return AgentPackageRemovalResult.InUse;
        await files.RemoveCachedAsync(selected.Sha256, selected.ManifestSha256,
            related.Where(p => p.Id != packageId).Select(p => p.ManifestSha256).ToArray(), token);
        await transaction.CommitAsync(token);
        return AgentPackageRemovalResult.Removed;
    }
}
