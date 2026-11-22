using System.Security.Cryptography;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Stores GitHub packages and manifests for offline Agent updates.</summary>
public sealed class PackageLibraryTransport(string cacheDirectory) : IPackageLibraryTransport
{
    private readonly string cacheDirectory = Path.GetFullPath(cacheDirectory);

    public bool IsCached(string archiveHash, string manifestHash) =>
        PackageRules.IsHash(archiveHash) && PackageRules.IsHash(manifestHash) &&
        File.Exists(ArchivePath(archiveHash)) && File.Exists(ManifestPath(manifestHash));

    public async Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] manifest,
        GitHubAgentReleases github, CancellationToken token)
    {
        Directory.CreateDirectory(cacheDirectory);
        await using var guard = await ExclusiveLocalLock.AcquireAsync(Path.Combine(cacheDirectory, ".cache.lock"), token);
        foreach (var partial in Directory.EnumerateFiles(cacheDirectory, "*.download")) File.Delete(partial);
        foreach (var partial in Directory.EnumerateFiles(cacheDirectory, "*.manifest.json.tmp")) File.Delete(partial);
        var archive = ArchivePath(asset.Sha256);
        var valid = await OpenVerifiedAsync(archive, asset.Sha256, asset.Size, token);
        if (valid is not null) await valid.DisposeAsync();
        else
        {
            if (File.Exists(archive)) File.Delete(archive);
            var temporary = Path.Combine(cacheDirectory, $"{Guid.CreateVersion7():N}.download");
            try
            {
                await github.DownloadAsync(version, asset, temporary, token);
                File.Move(temporary, archive);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var path = ManifestPath(manifestHash);
        var staged = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(staged, manifest, token);
            File.Move(staged, path, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    public async Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token)
    {
        if (!PackageRules.IsHash(hash)) return null;
        try
        {
            var content = await File.ReadAllBytesAsync(ManifestPath(hash), token);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(hash)) ? content : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token) =>
        PackageRules.IsHash(hash) ? OpenVerifiedAsync(ArchivePath(hash), hash, size, token) : Task.FromResult<Stream?>(null);

    public async Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token)
    {
        if (!PackageRules.IsHash(archiveHash) || !PackageRules.IsHash(manifestHash)) throw new ArgumentException("Invalid package hash.");
        if (!Directory.Exists(cacheDirectory)) return;
        await using var guard = await ExclusiveLocalLock.AcquireAsync(Path.Combine(cacheDirectory, ".cache.lock"), token);
        if (!otherManifests.Contains(manifestHash, StringComparer.Ordinal)) File.Delete(ManifestPath(manifestHash));
        if (!otherManifests.Any(hash => File.Exists(ManifestPath(hash)))) File.Delete(ArchivePath(archiveHash));
    }

    private async Task<Stream?> OpenVerifiedAsync(string path, string hash, long size, CancellationToken token)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                if (stream.Length == size && CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, token), Convert.FromHexString(hash)))
                {
                    stream.Position = 0;
                    return stream;
                }
                await stream.DisposeAsync();
                return null;
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private string ArchivePath(string hash) => Path.Combine(cacheDirectory, hash + ".pkg");
    private string ManifestPath(string hash) => Path.Combine(cacheDirectory, hash + ".manifest.json");
}
