namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>Internal generation and cleanup state for one service and UTC five-minute window.</summary>
public sealed class MetricRollupWindow
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public long InputRevision { get; set; }
    public long? ProjectedRevision { get; set; }
    public int? ProjectionVersion { get; set; }
    public DateTimeOffset? ProjectedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool RawPruned { get; set; }
    public byte[] Version { get; private set; } = [];
}
