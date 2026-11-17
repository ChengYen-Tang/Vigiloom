namespace Vigiloom.UpdateSecurity;

/// <summary>Acquires the signed manifest and verified archive for a centrally assigned release.</summary>
/// <remarks>The caller must check the current assignment and revocation before switching, and keep
/// trust and cache directories writable only by the Updater service.</remarks>
public sealed class CentralPackageAcquirer
{
    private readonly Uri metadataBase;
    private readonly Uri targetsBase;
    private readonly Uri packageBase;
    private readonly string trustDirectory;
    private readonly string cacheDirectory;
    private readonly long cacheBytes;
    private readonly TimeProvider clock;
    private readonly HttpClient http;

    public CentralPackageAcquirer(Uri server, string trustDirectory, string cacheDirectory, long cacheBytes, TimeProvider clock)
        : this(server, trustDirectory, cacheDirectory, cacheBytes, clock, UpdateHttp.Client) { }

    internal CentralPackageAcquirer(Uri server, string trustDirectory, string cacheDirectory, long cacheBytes, TimeProvider clock, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("An HTTPS Agent server without credentials, query or fragment is required.", nameof(server));
        var origin = new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        metadataBase = new(origin, "api/v1/agent/update/metadata/");
        targetsBase = new(origin, "api/v1/agent/update/targets/");
        packageBase = new(origin, "api/v1/agent/update/packages/");
        this.trustDirectory = Path.GetFullPath(trustDirectory);
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.cacheBytes = cacheBytes > 0 ? cacheBytes : throw new ArgumentOutOfRangeException(nameof(cacheBytes));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<AcquiredPackage> AcquireAsync(string manifestSha256, string expectedVersion, string runtimeIdentifier,
        string updaterVersion, string? currentAgentVersion, string credential, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var repository = new BoundedTufRepository(metadataBase, targetsBase, http, agentCredential: credential);
        var reader = new TrustedManifestReader(metadataBase, targetsBase, repository);
        VerifiedManifest verified;
        await using (var lease = await new LocalTrustStore(trustDirectory).AcquireAsync(token))
            verified = await reader.ReadAsync(lease, manifestSha256, token);
        var manifest = PackageManifest.Parse(verified, clock.GetUtcNow());
        if (manifest.Version != expectedVersion || !manifest.IsCompatible(runtimeIdentifier, updaterVersion, currentAgentVersion, clock.GetUtcNow()))
            throw new InvalidDataException("The assigned package is incompatible with this Agent.");
        // The updater needs only the assigned archive. Old versions for rollback are already
        // unpacked, so remove other re-downloadable archives before applying the cache quota.
        Directory.CreateDirectory(cacheDirectory);
        await using (await ExclusiveLocalLock.AcquireAsync(Path.Combine(cacheDirectory, ".cache.lock"), token))
        {
            foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.pkg"))
            {
                var hash = Path.GetFileNameWithoutExtension(path);
                if (PackageRules.IsHash(hash) && hash != manifest.Sha256) File.Delete(path);
            }
        }
        var downloader = new PackageArchiveDownloader(packageBase, http, credential);
        var cache = new LocalPackageCache(cacheDirectory, cacheBytes, downloader, clock);
        var archive = await cache.OpenManifestAsync(manifest, token);
        return new AcquiredPackage(verified, archive);
    }
}

/// <summary>Caller-owned verified archive and its publisher-authenticated manifest.</summary>
public sealed class AcquiredPackage(VerifiedManifest manifest, Stream archive) : IAsyncDisposable
{
    public VerifiedManifest Manifest { get; } = manifest;
    public Stream Archive { get; } = archive;
    public ValueTask DisposeAsync() => Archive.DisposeAsync();
}
