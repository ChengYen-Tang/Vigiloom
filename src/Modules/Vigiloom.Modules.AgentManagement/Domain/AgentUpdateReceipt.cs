using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>An authenticated, deduplicated observation of one update attempt.</summary>
public sealed class AgentUpdateReceipt
{
    public long RowId { get; set; }
    public Guid AgentId { get; set; }
    public Guid MessageId { get; set; }
    public Guid UpdateId { get; set; }
    public int Sequence { get; set; }
    public AgentUpdateResultKind Result { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool RetryAllowed { get; set; }
    public AgentUpdateFailure? Failure { get; set; }
}
