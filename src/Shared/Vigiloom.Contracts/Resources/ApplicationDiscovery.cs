namespace Vigiloom.Contracts.Resources;

/// <summary>Identity metadata observed by an authenticated telemetry receiver.</summary>
public sealed record ApplicationObservation(Guid SourceId, string ServiceKey, string Environment, string DisplayName, string ServiceNamespace);
public sealed record ApplicationCandidateInfo(Guid Id, Guid SourceId, string ServiceKey, string Environment,
    string DisplayName, string ServiceNamespace, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);
public enum ApplicationObservationStatus { Recorded, AlreadyApproved }
