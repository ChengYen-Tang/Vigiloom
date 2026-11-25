using Vigiloom.Infrastructure.AgentPackages;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Tests.Fixtures;

// Assignment-only tests do not exercise package transport.
internal sealed class AvailablePackageTransport : IPackageLibraryTransport
{
    public bool IsCached(string archiveHash, string manifestHash) => true;
    public Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] manifest, GitHubAgentReleases github, CancellationToken token) => throw new NotSupportedException();
    public Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token) => throw new NotSupportedException();
    public Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token) => throw new NotSupportedException();
    public Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token) => throw new NotSupportedException();
}
