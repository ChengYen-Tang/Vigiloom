namespace Vigiloom.Modules.IdentityAccess.Domain;

/// <summary>An immutable account-operation record; account IDs remain available after account deletion.</summary>
public sealed class AccountAuditEvent
{
    private AccountAuditEvent() { }
    public AccountAuditEvent(Guid? actorId, Guid subjectId, string action, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (subjectId == Guid.Empty || actorId == Guid.Empty) throw new ArgumentException("Account identifiers must not be empty.");
        ActorId = actorId;
        SubjectId = subjectId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
    }
    public long RowId { get; private set; }
    public Guid? ActorId { get; private set; }
    public Guid SubjectId { get; private set; }
    public string Action { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
