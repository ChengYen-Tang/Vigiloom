using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Vigiloom.UpdateSecurity;

/// <summary>Acquires the manifest and verified archive for a centrally assigned release.</summary>
/// <remarks>The caller must check the current assignment and revocation before switching, and keep
/// trust and cache directories writable only by the Updater service.</remarks>
public sealed class CentralPackageAcquirer
{
    private readonly Uri targetsBase;
    private readonly Uri packageBase;
    private readonly string cacheDirectory;
    private readonly TimeProvider clock;
    private readonly HttpClient http;

    public CentralPackageAcquirer(Uri server, string cacheDirectory, TimeProvider clock)
        : this(server, cacheDirectory, clock, UpdateHttp.Client) { }

    internal CentralPackageAcquirer(Uri server, string cacheDirectory, TimeProvider clock, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("An HTTPS Agent server without credentials, query or fragment is required.", nameof(server));
        var origin = new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        targetsBase = new(origin, "api/v1/agent/update/targets/");
        packageBase = new(origin, "api/v1/agent/update/packages/");
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<AcquiredPackage> AcquireAsync(string manifestSha256, string expectedVersion, string runtimeIdentifier,
        string credential, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        if (!PackageRules.IsHash(manifestSha256)) throw new ArgumentException("A SHA-256 manifest identifier is required.", nameof(manifestSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(targetsBase, manifestSha256 + ".json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        var content = await response.Content.ReadAsByteArrayAsync(token);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(manifestSha256)))
            throw new InvalidDataException("The assigned manifest hash does not match.");
        var verified = new VerifiedManifest(content);
        var manifest = PackageManifest.Parse(verified);
        if (manifest.Version != expectedVersion || manifest.RuntimeIdentifier != runtimeIdentifier)
            throw new InvalidDataException("The assigned package does not match this Agent.");
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
        var cache = new LocalPackageCache(cacheDirectory, downloader, clock);
        var archive = await cache.OpenManifestAsync(manifest, token);
        return new AcquiredPackage(verified, archive);
    }
}

/// <summary>Caller-owned verified archive and its hash-verified manifest.</summary>
public sealed class AcquiredPackage(VerifiedManifest manifest, Stream archive) : IAsyncDisposable
{
    public VerifiedManifest Manifest { get; } = manifest;
    public Stream Archive { get; } = archive;
    public ValueTask DisposeAsync() => Archive.DisposeAsync();
}
