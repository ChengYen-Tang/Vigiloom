namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

public sealed class TraceSpan
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? InstanceId { get; set; }
    public long ContextId { get; set; }
    public TraceContext Context { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required byte[] TraceId { get; set; }
    public required byte[] SpanId { get; set; }
    public byte[]? ParentSpanId { get; set; }
    public required byte[] SpanPayload { get; set; }
}
