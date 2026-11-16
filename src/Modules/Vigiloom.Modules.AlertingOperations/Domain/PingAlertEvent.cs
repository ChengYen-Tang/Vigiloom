namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A durable outage or recovery event for notification and history.</summary>
public sealed class PingAlertEvent
{
    public long RowId { get; private set; }
    public Guid MessageId { get; set; } = Guid.CreateVersion7();
    public Guid AlertId { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool IsRecovery { get; set; }
    public required string DeviceName { get; set; }
    public required string GroupsJson { get; set; }
}
