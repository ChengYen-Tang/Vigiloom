namespace Vigiloom.Contracts.Operations;

/// <summary>A quota change requested by an authorized administrator.</summary>
public sealed record LogQuotaUpdate(LogQuotaSettings Settings, Guid ActorId);
