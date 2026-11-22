using System.Security.Cryptography;
using Tuf;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Serves a cached manifest and the currently accepted metadata to the TUF verifier.</summary>
internal sealed class CachedManifestRepository(ITrustMetadataLease lease, string hash, byte[] manifest) : ITufRepository
{
    public Task<byte[]?> FetchMetadataAsync(string role, int? version = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (role is not ("root" or "timestamp" or "snapshot" or "targets") || version is <= 0)
            throw new ArgumentException("An accepted TUF role and positive version are required.");
        var data = lease.LoadMetadata(role);
        if (data is null || version is not null && DurableTufCache.ValidateMetadata(role, data) != version)
            return Task.FromResult<byte[]?>(null);
        return Task.FromResult<byte[]?>(data);
    }

    public Task<byte[]?> FetchTargetAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = hash + ".json";
        var addressedName = Convert.ToHexStringLower(SHA256.HashData(manifest)) + "." + name;
        return Task.FromResult<byte[]?>(targetPath == name || targetPath == addressedName ? manifest : null);
    }
}
