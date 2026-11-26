namespace Vigiloom.Contracts.Operations;

/// <summary>Requests cleanup using the current application Log retention setting.</summary>
public sealed record ApplicationLogCleanupRequest(int RetentionDays);
public sealed record ApplicationLogCleanupResult(int Logs, int Contexts, bool HasMore);

/// <summary>One authorized application Log scope forwarded by the observability service.</summary>
public sealed record ApplicationLogSubmission(Guid SourceId, Guid ServiceId, Guid? InstanceId,
    byte[] ResourcePayload, byte[] ScopePayload, string ResourceSchemaUrl, string ScopeSchemaUrl,
    IReadOnlyList<ApplicationLogItem> Items);

/// <summary>Original protocol data and resolved timing for one application Log.</summary>
public sealed record ApplicationLogItem(DateTimeOffset OccurredAt, bool UsedReceivedTime, bool UsedObservedTime,
    byte[]? TraceId, byte[]? SpanId, byte[] RecordPayload);
