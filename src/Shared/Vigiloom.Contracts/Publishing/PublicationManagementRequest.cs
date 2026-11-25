namespace Vigiloom.Contracts.Publishing;

/// <summary>Administrator operation forwarded over the trusted internal service network.</summary>
public sealed record PublicationManagementRequest<T>(T Input, Guid ActorId);
