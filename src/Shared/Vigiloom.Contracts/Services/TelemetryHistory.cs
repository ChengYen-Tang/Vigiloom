namespace Vigiloom.Contracts.Services;

/// <summary>A page of telemetry and the shared OTLP context referenced by each item.</summary>
public sealed record TelemetryPage<TItem, TContext>(IReadOnlyList<TItem> Items, IReadOnlyList<TContext> Contexts, bool HasMore, bool Truncated);

/// <summary>Base64-encoded protobuf resource and instrumentation scope messages, plus their schema URLs.</summary>
public sealed record TelemetryContext(byte[] Resource, byte[] Scope, string ResourceSchemaUrl, string ScopeSchemaUrl);

/// <summary>The OTLP context and Metric metadata shared by one or more metric points.</summary>
public sealed record MetricTelemetryContext(TelemetryContext Context, byte[] Metric);

/// <summary>A log record, including its original OTLP LogRecord protobuf and correlation identifiers.</summary>
public sealed record TelemetryLog(Guid? InstanceId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt,
    bool UsedReceivedTime, bool UsedObservedTime, string? TraceId, string? SpanId, int Context, byte[] Record);

/// <summary>A span, including its original OTLP Span protobuf and hexadecimal identifiers.</summary>
public sealed record TelemetrySpan(Guid? InstanceId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, DateTimeOffset ReceivedAt,
    string TraceId, string SpanId, string? ParentSpanId, int Context, byte[] Span);

/// <summary>An OTLP metric data point. Kind identifies the original OTLP point message type.</summary>
public sealed record TelemetryMetricPoint(Guid? InstanceId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt,
    byte Kind, int Context, byte[] Point);
