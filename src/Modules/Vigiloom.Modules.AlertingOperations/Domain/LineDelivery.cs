using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A LINE request and its stable retry key; acceptance does not mean a user read it.</summary>
public sealed class LineDelivery
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid? NotificationId { get; set; }
    public Guid GroupId { get; set; }
    public required string Recipient { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FirstAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureCode { get; set; }
}
