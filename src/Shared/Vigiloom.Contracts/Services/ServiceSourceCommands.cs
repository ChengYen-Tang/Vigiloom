namespace Vigiloom.Contracts.Services;

public sealed record IssueServiceSourceCommand(string Name, Guid ActorId);
public sealed record RevokeServiceSourceCommand(Guid ActorId);
public sealed record RotateServiceSourceCommand(byte[] Version, Guid ActorId);
