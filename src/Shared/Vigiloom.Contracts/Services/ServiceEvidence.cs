namespace Vigiloom.Contracts.Services;

public sealed record ServiceLogEvidence(DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, bool UsedReceivedTime, bool UsedObservedTime, Guid? InstanceId,
    string Severity, string Body, bool BodyTruncated, string? TraceId, string? SpanId);
public sealed record ServiceTraceEvidence(DateTimeOffset StartedAt, DateTimeOffset EndedAt, DateTimeOffset ReceivedAt,
    Guid? InstanceId, string Name, string Status, string TraceId, string SpanId, string? ParentSpanId);
public sealed record ServiceEvidencePage<T>(IReadOnlyList<T> Items, bool HasMore, bool Truncated);
