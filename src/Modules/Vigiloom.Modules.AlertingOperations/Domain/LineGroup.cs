using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A LINE group discovered by webhook and explicitly approved by an administrator.</summary>
public sealed class LineGroup
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public required string ExternalId { get; set; }
    public string? Name { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
