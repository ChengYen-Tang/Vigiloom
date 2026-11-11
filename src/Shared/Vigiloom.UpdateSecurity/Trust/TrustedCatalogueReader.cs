using Tuf;

namespace Vigiloom.UpdateSecurity;

/// <summary>A catalogue accepted by TUF and the expiration of its authorizing metadata.</summary>
public sealed class VerifiedCatalogue
{
    internal VerifiedCatalogue(ReadOnlyMemory<byte> content, DateTimeOffset expires)
    {
        Content = content;
        Expires = expires;
    }

    public ReadOnlyMemory<byte> Content { get; }
    public DateTimeOffset Expires { get; }
}

/// <summary>Verifies a signed catalogue while a caller holds its durable trust-state lease.</summary>
public sealed class TrustedCatalogueReader
{
    private readonly Uri metadataBase;
    private readonly Uri targetsBase;
    private readonly ITufRepository repository;

    public TrustedCatalogueReader(Uri metadataBase, Uri targetsBase)
        : this(metadataBase, targetsBase, new BoundedTufRepository(metadataBase, targetsBase)) { }
    public TrustedCatalogueReader(Uri metadataBase, Uri targetsBase, string bearerCredential)
        : this(metadataBase, targetsBase, new BoundedTufRepository(metadataBase, targetsBase, bearerCredential)) { }

    internal TrustedCatalogueReader(Uri metadataBase, Uri targetsBase, ITufRepository repository)
    {
        this.metadataBase = metadataBase ?? throw new ArgumentNullException(nameof(metadataBase));
        this.targetsBase = targetsBase ?? throw new ArgumentNullException(nameof(targetsBase));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <remarks>
    /// The caller owns the exclusive lease for the complete operation. No catalogue may be used
    /// after its authorizing metadata expires. A failed metadata write aborts this operation;
    /// a fresh client must be constructed for every subsequent attempt.
    /// </remarks>
    public async Task<VerifiedCatalogue> ReadAsync(ITrustMetadataLease lease, CancellationToken cancellationToken = default)
    {
        var result = await ReadTargetAsync(lease, "catalog.json", cancellationToken).ConfigureAwait(false);
        return new VerifiedCatalogue(result.Content, result.Expires);
    }

    internal async Task<(ReadOnlyMemory<byte> Content, DateTimeOffset Expires)> ReadTargetAsync(
        ITrustMetadataLease lease, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var cache = new DurableTufCache(lease);
        var trustedRoot = cache.LoadMetadata("root")!;
        using var client = new TufClient(new TufClientOptions
        {
            MetadataBaseUrl = metadataBase,
            TargetsBaseUrl = targetsBase,
            TrustedRoot = trustedRoot,
            Repository = repository,
            Cache = cache,
            MaxRootRotations = 32,
            MaxDelegations = 0
        });
        var result = await client.GetTargetAsync(target, cancellationToken).ConfigureAwait(false);
        await lease.VerifyHeldAsync(cancellationToken).ConfigureAwait(false);
        return (result.Content, result.Metadata.Expires);
    }
}
