namespace Vigiloom.UpdateSecurity;

/// <summary>Exclusively holds one repository's persistent trust state throughout a TUF operation.</summary>
/// <remarks>
/// The implementation must distinguish never-stored roles from missing or corrupt previously stored
/// data, and throw on I/O, integrity, or lease failure. Root metadata must be explicitly provisioned
/// before normal use. StoreMetadata must durably commit before returning and must not roll back an
/// accepted version except when an authenticated root rotates the applicable role's authority.
/// The lease must exclude other processes and, centrally, other platform nodes. Successful callers
/// must verify the lease, and any later persistent publication must itself be guarded by that lease.
/// </remarks>
public interface ITrustMetadataLease : IAsyncDisposable
{
    byte[]? LoadMetadata(string role);
    void StoreMetadata(string role, ReadOnlySpan<byte> data);
    Task VerifyHeldAsync(CancellationToken cancellationToken = default);
}
