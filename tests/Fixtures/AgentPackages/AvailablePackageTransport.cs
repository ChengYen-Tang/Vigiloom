using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Tests.Fixtures;

// Assignment-only tests do not exercise package transport.
internal sealed class AvailablePackageTransport : IPackageLibraryTransport
{
    public bool IsCached(string archiveHash, string manifestHash) => true;
    public Task<VerifiedManifest> ReadManifestAsync(Uri origin, string? credential, ITrustMetadataLease lease, string hash, CancellationToken token) => throw new NotSupportedException();
    public Task CacheAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken token) => throw new NotSupportedException();
    public Task SaveManifestAsync(VerifiedManifest verified, PackageManifest manifest, CancellationToken token) => throw new NotSupportedException();
    public Task<VerifiedManifest?> ReadCachedManifestAsync(ITrustMetadataLease lease, string hash, CancellationToken token) => throw new NotSupportedException();
    public Task<Stream?> OpenCachedAsync(PackageManifest manifest, CancellationToken token) => throw new NotSupportedException();
    public Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token) => throw new NotSupportedException();
    public Task<Stream> OpenAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken token) => throw new NotSupportedException();
}
