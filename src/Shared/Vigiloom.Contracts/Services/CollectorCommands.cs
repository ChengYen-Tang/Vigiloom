namespace Vigiloom.Contracts.Services;

public sealed record RegisterCollectorCommand(string Name, Guid ServiceId, bool DedicatedSourceConfirmed, Guid ActorId);
public sealed record SetCollectorRouteCommand(Guid SourceId, string Version, string? Exporter, bool Add, Guid ActorId);
