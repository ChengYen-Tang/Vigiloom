using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class PackageLibraryTransport : IPackageLibraryTransport
{
    private readonly string cacheDirectory;
    private readonly long maximumBytes;
    private readonly TimeProvider clock;
    private readonly HttpClient? client;

    public PackageLibraryTransport(string cacheDirectory, long maximumBytes, TimeProvider clock)
    {
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal PackageLibraryTransport(string cacheDirectory, long maximumBytes, TimeProvider clock, HttpClient client)
        : this(cacheDirectory, maximumBytes, clock) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<VerifiedManifest> ReadManifestAsync(Uri origin, string? credential, ITrustMetadataLease lease,
        string manifestSha256, CancellationToken cancellationToken)
    {
        var metadata = new Uri(origin, "metadata/");
        var targets = new Uri(origin, "targets/");
        var reader = client is not null ? new TrustedManifestReader(metadata, targets, new BoundedTufRepository(metadata, targets, client, agentCredential: credential)) :
            credential is null ? new TrustedManifestReader(metadata, targets) : new TrustedManifestReader(metadata, targets, credential);
        return reader.ReadAsync(lease, manifestSha256, cancellationToken);
    }

    public async Task CacheAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken cancellationToken)
    {
        var packages = new Uri(origin, "packages/");
        var downloader = client is not null ? new PackageArchiveDownloader(packages, client, credential) :
            credential is null ? new PackageArchiveDownloader(packages) : new PackageArchiveDownloader(packages, credential);
        var cache = new LocalPackageCache(cacheDirectory, maximumBytes, downloader, clock);
        await using var stream = await cache.OpenManifestAsync(manifest, cancellationToken);
    }
}
