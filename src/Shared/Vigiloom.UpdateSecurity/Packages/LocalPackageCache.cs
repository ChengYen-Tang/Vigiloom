using System.Security.Cryptography;

namespace Vigiloom.UpdateSecurity;

/// <summary>A node-local cache of hash-verified package archives.</summary>
public sealed class LocalPackageCache(string directory, PackageArchiveDownloader downloader, TimeProvider clock)
{
    private readonly string directory = Path.GetFullPath(directory);
    private readonly PackageArchiveDownloader downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    private readonly TimeProvider clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <remarks>The caller must separately enforce assignment, current revocation and platform compatibility.</remarks>
    public Task<Stream> OpenManifestAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return OpenCoreAsync(new(manifest.Sha256, manifest.Size),
            token => downloader.DownloadManifestAsync(manifest, directory, clock, token), cancellationToken);
    }

    /// <summary>Opens an already cached archive without contacting the package source.</summary>
    public async Task<Stream?> OpenCachedManifestAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var entry = new CacheEntry(manifest.Sha256, manifest.Size);
        var path = Path.Combine(directory, entry.Sha256 + ".pkg");
        try
        {
            var stream = await TryOpenVerifiedAsync(path, entry, cancellationToken);
            if (stream is null) return null;
            return stream;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Removes downloads left by a terminated process without touching cached packages.</summary>
    public static async Task CleanupInterruptedDownloadsAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) return;
        await using var cacheLock = await ExclusiveLocalLock.AcquireAsync(Path.Combine(directory, ".cache.lock"), cancellationToken);
        RemovePartialDownloads(directory);
    }

    private async Task<Stream> OpenCoreAsync(CacheEntry entry, Func<CancellationToken, Task<string>> download, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, entry.Sha256 + ".pkg");
        if (File.Exists(destination))
        {
            try
            {
                var cached = await TryOpenVerifiedAsync(destination, entry, cancellationToken);
                if (cached is not null)
                {
                    return cached;
                }
            }
            catch (FileNotFoundException) { /* Another process replaced a corrupt entry. */ }
        }
        await using var cacheLock = await ExclusiveLocalLock.AcquireAsync(Path.Combine(directory, ".cache.lock"), cancellationToken);
        // Only the lock holder downloads, so any old staging file belongs to an interrupted download.
        RemovePartialDownloads(directory);
        if (File.Exists(destination))
        {
            var verified = await TryOpenVerifiedAsync(destination, entry, cancellationToken);
            if (verified is not null)
            {
                return verified;
            }
            File.Delete(destination);
        }

        var staging = await download(cancellationToken);
        try
        {
            File.Move(staging, destination, overwrite: false);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
        var stream = await TryOpenVerifiedAsync(destination, entry, cancellationToken)
            ?? throw new InvalidDataException("The cached package failed verification.");
        return stream;
    }

    private static void RemovePartialDownloads(string directory)
    {
        foreach (var partial in Directory.EnumerateFiles(directory, "*.download")) File.Delete(partial);
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

    private readonly record struct CacheEntry(string Sha256, long Size);
}
