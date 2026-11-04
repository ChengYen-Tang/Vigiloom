namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>A single accepted OTLP log; original protocol messages remain intact for interpretation.</summary>
public sealed class LogRecord
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public long ContextId { get; set; }
    public LogContext Context { get; set; } = null!;
    public Guid? InstanceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool UsedReceivedTime { get; set; }
    public bool UsedObservedTime { get; set; }
    public byte[]? TraceId { get; set; }
    public byte[]? SpanId { get; set; }
    public required byte[] RecordPayload { get; set; }
}
