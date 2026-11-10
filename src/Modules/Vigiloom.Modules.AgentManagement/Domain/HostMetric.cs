using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A single timestamped host metric received from an authenticated Agent.</summary>
public sealed class HostMetric
{
    private HostMetric() { }

    public HostMetric(Guid agentId, Guid batchId, int ordinal, AgentMetricPoint point, DateTimeOffset receivedAt)
    {
        AgentId = agentId; BatchId = batchId; MessageId = point.MessageId; Ordinal = ordinal;
        OccurredAt = point.OccurredAt; ReceivedAt = receivedAt.ToUniversalTime(); Kind = point.Kind;
        Source = point.Source; Dimension = point.Dimension; Value = point.Value;
    }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid MessageId { get; private set; }
    public int Ordinal { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public AgentMetricKind Kind { get; private set; }
    public string Source { get; private set; } = "";
    public string? Dimension { get; private set; }
    public double Value { get; private set; }

    public bool Matches(AgentMetricPoint point) => OccurredAt == point.OccurredAt && Kind == point.Kind &&
        string.Equals(Source, point.Source, StringComparison.Ordinal) &&
        string.Equals(Dimension, point.Dimension, StringComparison.Ordinal) && Value == point.Value;
}
