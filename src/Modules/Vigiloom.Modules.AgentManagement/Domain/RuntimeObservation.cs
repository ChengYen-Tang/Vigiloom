using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A recorded Agent runtime observation, not a claim about individual collector capabilities.</summary>
public sealed class RuntimeObservation
{
    private RuntimeObservation() { }
    public RuntimeObservation(Guid agentId, Guid batchId, int ordinal, AgentRuntimeMessage item, DateTimeOffset receivedAt)
    {
        AgentId = agentId; BatchId = batchId; MessageId = item.MessageId; Ordinal = ordinal;
        OccurredAt = item.OccurredAt.ToUniversalTime(); ReceivedAt = receivedAt.ToUniversalTime();
        OperatingSystem = item.OperatingSystem; Architecture = item.Architecture; RuntimeVersion = item.RuntimeVersion;
    }
    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid MessageId { get; private set; }
    public int Ordinal { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string OperatingSystem { get; private set; } = "";
    public string Architecture { get; private set; } = "";
    public string RuntimeVersion { get; private set; } = "";

    public bool Matches(AgentRuntimeMessage item) => OccurredAt == item.OccurredAt &&
        string.Equals(OperatingSystem, item.OperatingSystem, StringComparison.Ordinal) &&
        string.Equals(Architecture, item.Architecture, StringComparison.Ordinal) &&
        string.Equals(RuntimeVersion, item.RuntimeVersion, StringComparison.Ordinal);
}
