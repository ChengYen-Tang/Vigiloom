using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Current centrally managed threshold for one exact metric series.</summary>
public sealed class HostMetricThreshold
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public Guid AgentId { get; set; }
    public AgentMetricKind Kind { get; set; }
    public string Source { get; set; } = "";
    public string? Dimension { get; set; }
    public byte[] SeriesHash { get; set; } = [];
    public bool Enabled { get; set; }
    public HostMetricComparison Comparison { get; set; }
    public double Value { get; set; }
    public int FreshnessSeconds { get; set; }
    public byte[] Version { get; private set; } = [];
}

/// <summary>Immutable record of a committed threshold change.</summary>
public sealed class HostMetricThresholdAudit
{
    public long RowId { get; private set; }
    public Guid ThresholdId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool Enabled { get; set; }
    public HostMetricComparison Comparison { get; set; }
    public double Value { get; set; }
    public int FreshnessSeconds { get; set; }
    public byte[] Version { get; set; } = [];
}
