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

    public bool IsCached(string archiveHash, string manifestHash) =>
        PackageRules.IsHash(archiveHash) && PackageRules.IsHash(manifestHash) &&
        File.Exists(Path.Combine(cacheDirectory, archiveHash + ".pkg")) && File.Exists(ManifestPath(manifestHash));

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
        await using var stream = await OpenAsync(origin, credential, manifest, cancellationToken);
    }

    public async Task SaveManifestAsync(VerifiedManifest verified, PackageManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = ManifestPath(manifest.ManifestSha256);
        await using var cacheLock = await ExclusiveLocalLock.AcquireAsync(Path.Combine(cacheDirectory, ".cache.lock"), cancellationToken);
        if (!File.Exists(Path.Combine(cacheDirectory, manifest.Sha256 + ".pkg")))
            throw new IOException("The downloaded package is no longer available.");
        var temporary = path + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, verified.Content.ToArray(), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<VerifiedManifest?> ReadCachedManifestAsync(ITrustMetadataLease lease, string hash, CancellationToken cancellationToken)
    {
        byte[] content;
        try { content = await File.ReadAllBytesAsync(ManifestPath(hash), cancellationToken); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (content.Length is 0 or > 1048576) throw new InvalidDataException("Invalid cached manifest size.");
        var origin = new Uri("https://localhost/");
        var repository = new CachedManifestRepository(lease, hash, content);
        return await new TrustedManifestReader(origin, origin, repository).ReadAsync(lease, hash, cancellationToken);
    }

    public Task<Stream?> OpenCachedAsync(PackageManifest manifest, CancellationToken cancellationToken)
    {
        var downloader = new PackageArchiveDownloader(new Uri("https://localhost/"));
        return new LocalPackageCache(cacheDirectory, maximumBytes, downloader, clock).OpenCachedManifestAsync(manifest, cancellationToken);
    }

    public async Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken cancellationToken)
    {
        if (!PackageRules.IsHash(archiveHash)) throw new ArgumentException("A lowercase SHA-256 archive identifier is required.", nameof(archiveHash));
        var manifest = ManifestPath(manifestHash);
        if (!Directory.Exists(cacheDirectory)) return;
        await using var cacheLock = await ExclusiveLocalLock.AcquireAsync(Path.Combine(cacheDirectory, ".cache.lock"), cancellationToken);
        if (!otherManifests.Contains(manifestHash, StringComparer.Ordinal)) File.Delete(manifest);
        if (!otherManifests.Any(hash => File.Exists(ManifestPath(hash))))
            File.Delete(Path.Combine(cacheDirectory, archiveHash + ".pkg"));
    }

    private string ManifestPath(string hash)
    {
        if (hash is not { Length: 64 } || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 manifest identifier is required.", nameof(hash));
        return Path.Combine(cacheDirectory, hash + ".manifest.json");
    }

    public Task<Stream> OpenAsync(Uri origin, string? credential, PackageManifest manifest, CancellationToken cancellationToken)
    {
        var packages = new Uri(origin, "packages/");
        var downloader = client is not null ? new PackageArchiveDownloader(packages, client, credential) :
            credential is null ? new PackageArchiveDownloader(packages) : new PackageArchiveDownloader(packages, credential);
        var cache = new LocalPackageCache(cacheDirectory, maximumBytes, downloader, clock);
        return cache.OpenManifestAsync(manifest, cancellationToken);
    }
}
