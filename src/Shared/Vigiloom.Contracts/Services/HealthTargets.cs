namespace Vigiloom.Contracts.Services;

/// <summary>Administrator settings for one approved application's HTTP health check.</summary>
public sealed record HealthTargetInput(string Endpoint, int IntervalSeconds, int TimeoutSeconds,
    int FailureThreshold, int RecoveryThreshold, bool Enabled, string Version);

/// <summary>A new health check associated with a service and optionally one known instance.</summary>
public sealed record CreateHealthTargetInput(Guid ServiceId, Guid? InstanceId, HealthTargetInput Settings);

/// <summary>Current administrator-only configuration and optimistic concurrency token.</summary>
public sealed record HealthTargetSnapshot(Guid Id, Guid ServiceId, Guid? InstanceId, string Endpoint,
    int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int RecoveryThreshold, bool Enabled, string Version);

/// <summary>Allowed destination networks and the last observed version.</summary>
public sealed record HealthNetworkPolicyInput(IReadOnlyList<string> Networks, string Version);

/// <summary>Effective network policy, generation and optimistic concurrency token.</summary>
public sealed record HealthNetworkPolicySnapshot(IReadOnlyList<string> Networks, long Generation, string Version);

/// <summary>An administrator-visible historical network policy revision.</summary>
public sealed record HealthNetworkRevisionSnapshot(long Generation, DateTimeOffset OccurredAt, Guid ActorId, IReadOnlyList<string> Networks);

public enum HealthCheckObservationKind { Healthy = 1, Degraded = 2, Unhealthy = 3, Timeout = 4, TransportError = 5, InvalidResponse = 6, NetworkDenied = 7 }
/// <summary>The latest recorded probe, which is not by itself a current service-health verdict.</summary>
public sealed record HealthCheckObservation(Guid TargetId, HealthCheckObservationKind Kind, DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt);
public sealed record HealthObservationPage(IReadOnlyList<HealthCheckObservation> Items, bool HasMore);

/// <summary>Evaluated application health; unknown includes missing, expired or invalidated evidence.</summary>
public enum ServiceHealthState { Unknown, Healthy, Degraded, Unhealthy }

/// <summary>Current aggregate health and the number of configured participants with unknown evidence.</summary>
[Flags]
public enum ServiceHealthUnknownReason { None = 0, NotConfigured = 1, Disabled = 2, Waiting = 4, Expired = 8, Invalidated = 16 }

public sealed record ServiceHealthStatus(ServiceHealthState State, int Participants, int UnknownParticipants,
    ServiceHealthUnknownReason UnknownReasons = ServiceHealthUnknownReason.None);

/// <summary>One UTC half-open interval of evaluated service health.</summary>
public sealed record ServiceHealthInterval(DateTimeOffset Start, DateTimeOffset End, ServiceHealthState State, int UnknownParticipants);

/// <summary>Service history with duration-based availability and data coverage, both expressed as fractions.</summary>
public sealed record ServiceHealthHistory(IReadOnlyList<ServiceHealthInterval> Intervals, double? Availability, double Coverage);
