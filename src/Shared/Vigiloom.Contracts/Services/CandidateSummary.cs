namespace Vigiloom.Contracts.Services;

/// <summary>Non-secret candidate metadata visible to an administrator.</summary>
public sealed record CandidateSummary(Guid Id, Guid SourceId, string ServiceKey, string Environment, string DisplayName, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);
