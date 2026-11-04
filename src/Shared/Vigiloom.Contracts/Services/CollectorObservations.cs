namespace Vigiloom.Contracts.Services;

/// <summary>Observed Collector uptime reports; they are not a pipeline health verdict.</summary>
public sealed record CollectorObservationPage(Guid ServiceId, Guid SourceId, DateTimeOffset? SourceRevokedAt,
    DateTimeOffset? ServiceStoppedAt, DateTimeOffset ObservedThrough, IReadOnlyList<CollectorInstanceObservation> Instances,
    DateTimeOffset? UnidentifiedReceivedAt, bool HasMore);

public sealed record CollectorInstanceObservation(Guid InstanceId, string ExternalInstanceId,
    DateTimeOffset? RetiredAt, DateTimeOffset? LastReceivedAt, DateTimeOffset? LastMeasuredAt);
