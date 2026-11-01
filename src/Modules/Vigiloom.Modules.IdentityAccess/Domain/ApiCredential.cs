using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.IdentityAccess.Domain;

/// <summary>A third-party API identity. The secret itself is never persisted.</summary>
public sealed class ApiCredential
{
    private ApiCredential() { }

    public ApiCredential(string name, byte[] verifier, bool canWrite, DateTimeOffset expiresAt, Guid createdBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || verifier.Length != 32 || createdBy == Guid.Empty)
            throw new ArgumentException("Invalid API credential fields.");
        Id = ResourceIds.Create();
        Name = name;
        Verifier = verifier;
        CanWrite = canWrite;
        ExpiresAt = expiresAt.ToUniversalTime();
        CreatedBy = createdBy;
        CreatedAt = UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public byte[] Verifier { get; private set; } = [];
    public bool CanWrite { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = UpdatedAt = now.ToUniversalTime();
    }
}
