namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

public sealed class MetricPoint
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? InstanceId { get; set; }
    public long ContextId { get; set; }
    public MetricContext Context { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public byte Kind { get; set; }
    public required byte[] PointPayload { get; set; }
}
