using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Downloads verified packages into the central cache without granting Agent download access.</summary>
public sealed class PackageLibrary(IDbContextFactory<AgentManagementDbContext> contexts, AgentPackageSourceSettings settings,
    UpdateTrustStore trust, IPackageLibraryTransport transport, TimeProvider clock)
{
    /// <summary>Returns false if the package or source is no longer eligible at any verification boundary.</summary>
    public async Task<bool> CacheAsync(Guid packageId, CancellationToken cancellationToken)
    {
        if (packageId == Guid.Empty) return false;
        var source = await settings.ReadEffectiveAsync(cancellationToken);
        if (source is null) return false;
        var before = await ReadEligibleAsync(packageId, source.Version, cancellationToken);
        if (before is null) return false;

        VerifiedManifest verified;
        await using (var lease = await trust.AcquireAsync(cancellationToken))
            verified = await transport.ReadManifestAsync(source.BaseUri, source.Credential, lease, before.ManifestSha256, cancellationToken);
        var manifest = PackageManifest.Parse(verified, clock.GetUtcNow());
        if (!Matches(before, manifest) || await ReadEligibleAsync(packageId, source.Version, cancellationToken) is not { } current ||
            !Matches(current, manifest)) return false;

        await transport.CacheAsync(source.BaseUri, source.Credential, manifest, cancellationToken);
        var after = await ReadEligibleAsync(packageId, source.Version, cancellationToken);
        if (after is null || !Matches(after, manifest) || manifest.Expires <= clock.GetUtcNow()) return false;
        await transport.SaveManifestAsync(verified, manifest, cancellationToken);
        return true;
    }

    /// <summary>Removes an unused local package while preserving files shared by other versions.</summary>
    public async Task<PackageRemovalResult> RemoveAsync(Guid packageId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidate = await db.Packages.AsNoTracking().Where(x => x.Id == packageId)
            .Select(x => new { x.Sha256 }).SingleOrDefaultAsync(cancellationToken);
        if (candidate is null) return PackageRemovalResult.NotFound;
        var related = await db.Packages.FromSqlInterpolated($"SELECT * FROM [agent].[Packages] WITH (UPDLOCK, HOLDLOCK) WHERE [Sha256] = {candidate.Sha256}")
            .ToArrayAsync(cancellationToken);
        var selected = related.SingleOrDefault(x => x.Id == packageId);
        if (selected is null) return PackageRemovalResult.NotFound;
        if (await db.VersionAssignments.AnyAsync(x => x.PackageId == packageId, cancellationToken) ||
            await db.Agents.AnyAsync(x => x.RevokedAt == null && x.RuntimeVersion == selected.Version &&
                x.RuntimeIdentifier == selected.RuntimeIdentifier, cancellationToken)) return PackageRemovalResult.InUse;
        var otherManifests = related.Where(x => x.Id != packageId).Select(x => x.ManifestSha256).ToArray();
        await transport.RemoveCachedAsync(selected.Sha256, selected.ManifestSha256, otherManifests, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PackageRemovalResult.Removed;
    }

    private async Task<PackageSnapshot?> ReadEligibleAsync(Guid id, byte[] sourceVersion, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow();
        var result = await (from package in context.Packages.AsNoTracking()
            from source in context.PackageSources.AsNoTracking()
            where package.Id == id && package.Listed && !package.Revoked && package.VerifiedUntil > now
            select new { package.Version, package.RuntimeIdentifier, package.Size, package.Sha256, package.ManifestSha256, SourceVersion = source.Version })
            .SingleOrDefaultAsync(token);
        if (result is null || !sourceVersion.AsSpan().SequenceEqual(result.SourceVersion)) return null;
        return new(result.Version, result.RuntimeIdentifier, result.Size, result.Sha256, result.ManifestSha256);
    }

    private static bool Matches(PackageSnapshot package, PackageManifest manifest) =>
        package.Version == manifest.Version && package.RuntimeIdentifier == manifest.RuntimeIdentifier && package.Size == manifest.Size &&
        package.Sha256 == manifest.Sha256 && package.ManifestSha256 == manifest.ManifestSha256;

    private sealed record PackageSnapshot(string Version, string RuntimeIdentifier, long Size, string Sha256, string ManifestSha256);
}

/// <summary>Obtains publisher-authenticated manifests and stores their archives in a bounded node-local cache.</summary>
public interface IPackageLibraryTransport
{
    bool IsCached(string archiveHash, string manifestHash);
    Task<VerifiedManifest> ReadManifestAsync(Uri origin, string? credential, ITrustMetadataLease lease, string manifestSha256, CancellationToken cancellationToken);
    Task CacheAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken cancellationToken);
    Task SaveManifestAsync(VerifiedManifest verified, PackageManifest manifest, CancellationToken cancellationToken);
    Task<VerifiedManifest?> ReadCachedManifestAsync(ITrustMetadataLease lease, string hash, CancellationToken cancellationToken);
    Task<Stream?> OpenCachedAsync(PackageManifest manifest, CancellationToken cancellationToken);
    Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken cancellationToken);
    /// <summary>Opens the verified cached archive; the caller owns the returned stream.</summary>
    Task<Stream> OpenAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken cancellationToken);
}

public enum PackageRemovalResult { Removed, InUse, NotFound }
