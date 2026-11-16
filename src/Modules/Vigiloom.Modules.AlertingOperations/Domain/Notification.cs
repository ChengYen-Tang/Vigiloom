using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A logical notification, awaiting an available delivery destination.</summary>
public sealed class Notification
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid MessageId { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? EvidenceOwnerId { get; set; }
    public Guid? EvidenceId { get; set; }
    public Guid? RuleTestId { get; set; }
}
