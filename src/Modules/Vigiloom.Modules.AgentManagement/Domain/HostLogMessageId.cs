namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Records a host Log message identity for cross-type deduplication.</summary>
public sealed class HostLogMessageId
{
    public Guid AgentId { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
