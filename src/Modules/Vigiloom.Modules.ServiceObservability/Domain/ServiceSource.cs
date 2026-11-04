namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>An independently authenticated telemetry source, not a person or host Agent.</summary>
public sealed class ServiceSource
{
    private ServiceSource() { }

    public ServiceSource(string name, byte[] verifier, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("A source name of at most 128 characters is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.Length != 32) throw new ArgumentException("The credential verifier must be 32 bytes.", nameof(verifier));
        Id = Guid.CreateVersion7();
        Name = name.Trim();
        Verifier = verifier.ToArray();
        CreatedAt = UpdatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = "";
    public byte[] Verifier { get; private set; } = [];
    public byte[] Version { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public void ReplaceVerifier(byte[] verifier, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.Length != 32) throw new ArgumentException("The credential verifier must be 32 bytes.", nameof(verifier));
        if (RevokedAt is not null) throw new InvalidOperationException("A revoked source cannot be rotated.");
        Verifier = verifier.ToArray();
        UpdatedAt = now.ToUniversalTime();
    }
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = UpdatedAt = now.ToUniversalTime();
    }
}
