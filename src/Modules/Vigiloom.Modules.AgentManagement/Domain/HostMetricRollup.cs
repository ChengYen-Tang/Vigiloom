using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Five-minute statistics for one Agent and exact metric series.</summary>
public sealed class HostMetricRollup
{
    private HostMetricRollup() { }

    internal HostMetricRollup(HostMetric point, DateTimeOffset start)
        => (AgentId, Kind, Source, Dimension, WindowStart) = (point.AgentId, point.Kind, point.Source, point.Dimension, start);

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public AgentMetricKind Kind { get; private set; }
    public string Source { get; private set; } = null!;
    public string? Dimension { get; private set; }
    public byte[] SourceBytes { get; private set; } = [];
    public byte[]? DimensionBytes { get; private set; }
    public DateTimeOffset WindowStart { get; private set; }
    public DateTimeOffset LastOccurredAt { get; private set; }
    public long Count { get; private set; }
    public double Minimum { get; private set; }
    public double Maximum { get; private set; }
    public double Sum { get; private set; }

    internal void Add(HostMetric point)
    {
        if (Count == 0)
        {
            LastOccurredAt = point.OccurredAt;
            Minimum = Maximum = point.Value;
        }
        else
        {
            if (point.OccurredAt > LastOccurredAt) LastOccurredAt = point.OccurredAt;
            Minimum = Math.Min(Minimum, point.Value);
            Maximum = Math.Max(Maximum, point.Value);
        }
        Count = checked(Count + 1);
        Sum += point.Value;
        point.MarkAggregated();
    }
}
