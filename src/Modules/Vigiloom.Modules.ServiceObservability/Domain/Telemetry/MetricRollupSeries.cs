namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

public enum MetricRollupMode : byte
{
    GaugeSnapshot = 1,
    DeltaSum = 2,
    CumulativeSumSnapshots = 3,
    DeltaHistogram = 4,
    SummarySnapshots = 5,
    Unsupported = 6,
    CumulativeHistogramSnapshots = 7
}

/// <summary>Internal type-preserving result for one series within a materialized window.</summary>
public sealed class MetricRollupSeries
{
    public long RowId { get; set; }
    public long WindowRowId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? InstanceId { get; set; }
    public required byte[] IdentityHash { get; set; }
    public required byte[] IdentityBytes { get; set; }
    public int CollisionOrdinal { get; set; }
    public MetricRollupMode Mode { get; set; }
    public long QualityFlags { get; set; }
    public bool RequiresRaw { get; set; }
    public int ResultVersion { get; set; }
    public int InputCount { get; set; }
    public long InputBytes { get; set; }
    public byte[]? ResultPayload { get; set; }
}
