using System.Security.Cryptography;
using Tuf;

namespace Vigiloom.UpdateSecurity;

/// <summary>Verifies only the content-addressed manifest selected for a particular Agent.</summary>
public sealed class TrustedManifestReader
{
    private readonly TrustedCatalogueReader reader;
    public TrustedManifestReader(Uri metadataBase, Uri targetsBase) => reader = new(metadataBase, targetsBase);
    public TrustedManifestReader(Uri metadataBase, Uri targetsBase, string agentCredential) =>
        reader = new(metadataBase, targetsBase, new BoundedTufRepository(metadataBase, targetsBase, agentCredential));
    internal TrustedManifestReader(Uri metadataBase, Uri targetsBase, ITufRepository repository) => reader = new(metadataBase, targetsBase, repository);

    public async Task<VerifiedManifest> ReadAsync(ITrustMetadataLease lease, string manifestSha256, CancellationToken cancellationToken = default)
    {
        if (manifestSha256 is not { Length: 64 } || manifestSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 manifest identifier is required.", nameof(manifestSha256));
        var (content, expires) = await reader.ReadTargetAsync(lease, manifestSha256 + ".json", cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content.Span), Convert.FromHexString(manifestSha256)))
            throw new InvalidDataException("The signed manifest does not match its identifier.");
        return new VerifiedManifest(content, expires);
    }
}

/// <summary>A specific TUF-verified manifest and its authorization expiry.</summary>
public sealed class VerifiedManifest
{
    internal VerifiedManifest(ReadOnlyMemory<byte> content, DateTimeOffset expires) => (Content, Expires) = (content, expires);
    public ReadOnlyMemory<byte> Content { get; }
    public DateTimeOffset Expires { get; }
}
