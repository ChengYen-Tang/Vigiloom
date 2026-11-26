namespace Vigiloom.Contracts.Operations;

/// <summary>Requests a retention preview on behalf of an administrator.</summary>
public sealed record RetentionPreviewChange(Guid ActorId, AuditRetentionPreviewRequest Settings);

/// <summary>Updates retention on behalf of an administrator.</summary>
public sealed record RetentionChange(Guid ActorId, AuditRetentionInput Settings);
