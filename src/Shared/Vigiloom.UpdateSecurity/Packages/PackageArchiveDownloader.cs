using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Vigiloom.UpdateSecurity;

/// <summary>Streams an authenticated catalogue entry to a temporary file and verifies it before returning.</summary>
public sealed class PackageArchiveDownloader
{
    private readonly Uri packageBase;
    private readonly HttpClient client;
    private readonly AuthenticationHeaderValue? authorization;

    public PackageArchiveDownloader(Uri packageBase) : this(packageBase, UpdateHttp.Client) { }
    public PackageArchiveDownloader(Uri packageBase, string agentCredential)
        : this(packageBase, UpdateHttp.Client, agentCredential ?? throw new ArgumentNullException(nameof(agentCredential))) { }

    internal PackageArchiveDownloader(Uri packageBase, HttpClient client, string? agentCredential = null)
    {
        ArgumentNullException.ThrowIfNull(packageBase);
        if (!packageBase.IsAbsoluteUri || packageBase.Scheme != Uri.UriSchemeHttps || packageBase.UserInfo.Length != 0 ||
            packageBase.Query.Length != 0 || packageBase.Fragment.Length != 0 || !packageBase.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("An HTTPS package base URI ending in a slash is required.", nameof(packageBase));
        this.packageBase = packageBase;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        if (agentCredential is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentCredential);
            authorization = new AuthenticationHeaderValue("Bearer", agentCredential);
        }
    }

    /// <remarks>The caller owns the returned temporary file, and must recheck expiry and revocation before use.</remarks>
    public Task<string> DownloadAsync(PackageCatalogue catalogue, PackageEntry entry, string temporaryDirectory,
        TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Revoked || !catalogue.Packages.Contains(entry))
            throw new InvalidOperationException("The package is not authorized by the current catalogue.");
        return DownloadCoreAsync(new(entry.Sha256, entry.Size, catalogue.Expires), temporaryDirectory, clock, cancellationToken);
    }

    /// <remarks>The caller must separately check assignment, revocation and platform compatibility before use.</remarks>
    public Task<string> DownloadManifestAsync(PackageManifest manifest, string temporaryDirectory,
        TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return DownloadCoreAsync(new(manifest.Sha256, manifest.Size, manifest.Expires), temporaryDirectory, clock, cancellationToken);
    }

    private async Task<string> DownloadCoreAsync(ArchiveDescriptor archive, string temporaryDirectory, TimeProvider clock, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await DownloadOnceAsync(archive, temporaryDirectory, clock, cancellationToken); }
            catch (HttpRequestException error) when (attempt < 3 && IsTransient(error) &&
                (error is not PackageSourceRetryException limited || limited.RetryAfter <= TimeSpan.FromSeconds(30)))
            {
                var delay = error is PackageSourceRetryException response && response.RetryAfter > TimeSpan.Zero
                    ? response.RetryAfter : TimeSpan.FromMilliseconds(200 * attempt + Random.Shared.Next(200));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpRequestException error) => error.StatusCode is null or
        System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests ||
        (int?)error.StatusCode >= 500;

    private async Task<string> DownloadOnceAsync(ArchiveDescriptor archive, string temporaryDirectory,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (archive.Expires <= clock.GetUtcNow()) throw new InvalidOperationException("The package authorization has expired.");
        Directory.CreateDirectory(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, $"{Guid.CreateVersion7():N}.download");
        var created = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(packageBase, archive.Sha256));
            request.Headers.Authorization = authorization;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable &&
                response.Headers.RetryAfter is { } retryAfter)
            {
                var delay = retryAfter.Delta ?? (retryAfter.Date - clock.GetUtcNow()) ?? TimeSpan.Zero;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                throw new PackageSourceRetryException(response.StatusCode, delay);
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != archive.Size)
                throw new InvalidDataException("Package length does not match signed metadata.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > archive.Size) throw new InvalidDataException("Package exceeds its signed size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                if (total != archive.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(archive.Sha256)))
                    throw new InvalidDataException("Package failed signed length or hash verification.");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                if (archive.Expires <= clock.GetUtcNow()) throw new InvalidDataException("The signed package metadata expired during download.");
                return path;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (Exception failure)
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Package download failed and its temporary file could not be removed: {path}",
                        new AggregateException(failure, cleanup));
                }
            }
            throw;
        }
    }

    private readonly record struct ArchiveDescriptor(string Sha256, long Size, DateTimeOffset Expires);
}
