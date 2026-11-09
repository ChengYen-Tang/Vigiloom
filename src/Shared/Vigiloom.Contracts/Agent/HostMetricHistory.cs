namespace Vigiloom.Contracts.Agent;

/// <summary>A single available host measurement; absent measurements are not represented as zero.</summary>
public sealed record HostMetricValue(DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, AgentMetricKind Kind,
    string Source, string? Dimension, double Value);

/// <summary>One bounded page of host measurements; the continuation token is opaque.</summary>
public sealed record HostMetricPage(IReadOnlyList<HostMetricValue> Items, string? NextCursor);

/// <summary>A populated UTC time bucket for a single Agent and metric series.</summary>
public sealed record HostMetricBucket(DateTimeOffset Start, DateTimeOffset LastObservedAt,
    double Minimum, double Maximum, double Average, long Samples);

/// <summary>Only populated buckets are returned; absence never means zero.</summary>
public sealed record HostMetricAggregate(Guid AgentId, string Source, string? Dimension,
    int BucketSeconds, IReadOnlyList<HostMetricBucket> Buckets);
