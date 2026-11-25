namespace Vigiloom.Infrastructure.AgentPackages;

public interface IPackageLibraryTransport
{
    bool IsCached(string archiveHash, string manifestHash);
    Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] manifest, GitHubAgentReleases github, CancellationToken token);
    Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token);
    Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token);
    Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token);
}
