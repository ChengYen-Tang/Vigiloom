namespace Vigiloom.Contracts.Services;

/// <summary>Read-only identity of an approved application service.</summary>
public sealed record ServiceDetails(Guid Id, string Name, string ServiceKey, string ServiceNamespace, string Environment, DateTimeOffset ApprovedAt, bool CollectionStopped, bool SourceRevoked,
    DateTimeOffset? LastLogReceivedAt, DateTimeOffset? LastMetricReceivedAt, DateTimeOffset? LastTraceReceivedAt, DateTimeOffset ServerTime);

/// <summary>A registered instance belonging to an approved application service.</summary>
public sealed record ServiceInstanceDetails(Guid Id, string Name, DateTimeOffset CreatedAt, bool Retired);

/// <summary>A bounded page of service instances.</summary>
public sealed record ServiceInstancePage(IReadOnlyList<ServiceInstanceDetails> Items, bool HasMore);
