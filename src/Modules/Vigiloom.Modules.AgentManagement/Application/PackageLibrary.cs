using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Downloads selected GitHub Agent releases to the central offline library.</summary>
public sealed class PackageLibrary(IDbContextFactory<AgentManagementDbContext> contexts, GitHubAgentReleases github,
    IPackageLibraryTransport transport)
{
    public async Task<bool> CacheAsync(string version, string runtimeIdentifier, CancellationToken token)
    {
        var asset = await github.GetAssetAsync(version, runtimeIdentifier, token);
        if (asset is null) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        var package = await db.Packages.SingleOrDefaultAsync(x => x.Version == version && x.RuntimeIdentifier == runtimeIdentifier, token);
        // An existing assignment identifies a particular release by this record. Do not
        // silently replace its content if an asset under the same GitHub tag changes.
        if (package is not null && (package.Sha256 != asset.Sha256 || package.Size != asset.Size))
            throw new InvalidOperationException("The release asset changed for an existing version. Publish a new version instead.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version, rid = runtimeIdentifier, size = asset.Size, sha256 = asset.Sha256
        });
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
        if (package is not null && package.ManifestSha256 != manifestHash)
        {
            // Keep the original manifest identity used by existing assignments.
            var existingManifest = await transport.ReadManifestAsync(package.ManifestSha256, token);
            if (existingManifest is not null)
            {
                manifest = existingManifest;
                manifestHash = package.ManifestSha256;
            }
            else
            {
                if (await db.VersionAssignments.AnyAsync(x => x.PackageId == package.Id, token))
                    throw new InvalidOperationException("The package manifest is missing. Clear its current assignments before downloading again.");
                package.ManifestSha256 = manifestHash;
            }
        }
        await transport.SaveAsync(version, asset, manifestHash, manifest, github, token);

        if (package is null)
        {
            package = new AgentPackage { Id = Guid.CreateVersion7(), Version = version, RuntimeIdentifier = runtimeIdentifier,
                Sha256 = asset.Sha256, ManifestSha256 = manifestHash, Size = asset.Size };
            db.Packages.Add(package);
        }
        await db.SaveChangesAsync(token);
        return true;
    }

    public async Task<PackageRemovalResult> RemoveAsync(Guid packageId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var candidate = await db.Packages.AsNoTracking().Where(x => x.Id == packageId).Select(x => new { x.Sha256 }).SingleOrDefaultAsync(token);
        if (candidate is null) return PackageRemovalResult.NotFound;
        var related = await db.Packages.FromSqlInterpolated($"SELECT * FROM [agent].[Packages] WITH (UPDLOCK, HOLDLOCK) WHERE [Sha256] = {candidate.Sha256}").ToArrayAsync(token);
        var selected = related.SingleOrDefault(x => x.Id == packageId);
        if (selected is null) return PackageRemovalResult.NotFound;
        if (await db.VersionAssignments.AnyAsync(x => x.PackageId == packageId, token) ||
            await db.Agents.AnyAsync(x => x.RevokedAt == null && x.RuntimeVersion == selected.Version && x.RuntimeIdentifier == selected.RuntimeIdentifier, token))
            return PackageRemovalResult.InUse;
        await transport.RemoveCachedAsync(selected.Sha256, selected.ManifestSha256,
            related.Where(x => x.Id != packageId).Select(x => x.ManifestSha256).ToArray(), token);
        await transaction.CommitAsync(token);
        return PackageRemovalResult.Removed;
    }
}

public interface IPackageLibraryTransport
{
    bool IsCached(string archiveHash, string manifestHash);
    Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] manifest, GitHubAgentReleases github, CancellationToken token);
    Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token);
    Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token);
    Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token);
}

public enum PackageRemovalResult { Removed, InUse, NotFound }
