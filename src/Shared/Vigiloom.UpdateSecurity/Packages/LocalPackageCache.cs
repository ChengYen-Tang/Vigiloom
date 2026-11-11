using System.Security.Cryptography;

namespace Vigiloom.UpdateSecurity;

/// <summary>A bounded, node-local cache of authenticated package archives.</summary>
public sealed class LocalPackageCache(string directory, long maximumBytes, PackageArchiveDownloader downloader, TimeProvider clock)
{
    private readonly string directory = Path.GetFullPath(directory);
    private readonly long maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private readonly PackageArchiveDownloader downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    private readonly TimeProvider clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Returns a verified stream owned by the caller. The directory must be private to one node.</summary>
    public Task<Stream> OpenAsync(PackageCatalogue catalogue, PackageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(entry);
        EnsureAuthorized(catalogue, entry);
        return OpenCoreAsync(new(entry.Sha256, entry.Size, catalogue.Expires),
            token => downloader.DownloadAsync(catalogue, entry, directory, clock, token), cancellationToken);
    }

    /// <remarks>The caller must separately enforce assignment, current revocation and platform compatibility.</remarks>
    public Task<Stream> OpenManifestAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return OpenCoreAsync(new(manifest.Sha256, manifest.Size, manifest.Expires),
            token => downloader.DownloadManifestAsync(manifest, directory, clock, token), cancellationToken);
    }

    private async Task<Stream> OpenCoreAsync(CacheEntry entry, Func<CancellationToken, Task<string>> download, CancellationToken cancellationToken)
    {
        EnsureUnexpired(entry);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, entry.Sha256 + ".pkg");
        if (File.Exists(destination))
        {
            try
            {
                var cached = await TryOpenVerifiedAsync(destination, entry, cancellationToken);
                if (cached is not null)
                {
                    try { EnsureUnexpired(entry); return cached; }
                    catch { await cached.DisposeAsync(); throw; }
                }
            }
            catch (FileNotFoundException) { /* Another process replaced a corrupt entry. */ }
        }
        await using var cacheLock = await ExclusiveLocalLock.AcquireAsync(Path.Combine(directory, ".cache.lock"), cancellationToken);
        // Only the lock holder downloads, so any old staging file belongs to an interrupted download.
        foreach (var partial in Directory.EnumerateFiles(directory, "*.download")) File.Delete(partial);
        if (File.Exists(destination))
        {
            var verified = await TryOpenVerifiedAsync(destination, entry, cancellationToken);
            if (verified is not null)
            {
                try { EnsureUnexpired(entry); return verified; }
                catch { await verified.DisposeAsync(); throw; }
            }
            File.Delete(destination);
        }

        long used = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.pkg"))
        {
            var size = new FileInfo(file).Length;
            if (size > maximumBytes - used) throw new IOException("The package cache quota has been exceeded.");
            used += size;
        }
        if (entry.Size > maximumBytes - used) throw new IOException("The package cache does not have enough quota.");
        var staging = await download(cancellationToken);
        try
        {
            EnsureUnexpired(entry);
            File.Move(staging, destination, overwrite: false);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
        var stream = await TryOpenVerifiedAsync(destination, entry, cancellationToken)
            ?? throw new InvalidDataException("The cached package failed verification.");
        try { EnsureUnexpired(entry); return stream; }
        catch { await stream.DisposeAsync(); throw; }
    }

    private void EnsureAuthorized(PackageCatalogue catalogue, PackageEntry entry)
    {
        if (catalogue.Expires <= clock.GetUtcNow() || entry.Revoked || !catalogue.Packages.Contains(entry))
            throw new InvalidOperationException("The package is not authorized by the current catalogue.");
    }

    private void EnsureUnexpired(CacheEntry entry)
    {
        if (entry.Expires <= clock.GetUtcNow()) throw new InvalidOperationException("The package authorization has expired.");
    }

    private static async Task<FileStream?> TryOpenVerifiedAsync(string path, CacheEntry entry, CancellationToken cancellationToken)
    {
        // A reader keeps the verified handle even if a different process replaces a corrupt path.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != entry.Size || !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(stream, cancellationToken), Convert.FromHexString(entry.Sha256)))
            {
                await stream.DisposeAsync();
                return null;
            }
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private readonly record struct CacheEntry(string Sha256, long Size, DateTimeOffset Expires);
}
