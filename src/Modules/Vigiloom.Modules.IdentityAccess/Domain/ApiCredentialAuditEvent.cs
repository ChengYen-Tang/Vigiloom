namespace Vigiloom.Modules.IdentityAccess.Domain;

/// <summary>Audit metadata for an API credential operation; never includes credential material.</summary>
public sealed class ApiCredentialAuditEvent
{
    private ApiCredentialAuditEvent() { }
    public ApiCredentialAuditEvent(Guid actorId, Guid credentialId, string action, DateTimeOffset occurredAt)
    {
        if (actorId == Guid.Empty || credentialId == Guid.Empty) throw new ArgumentException("Identifiers must not be empty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ActorId = actorId;
        CredentialId = credentialId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
    }
    public long RowId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CredentialId { get; private set; }
    public string Action { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
