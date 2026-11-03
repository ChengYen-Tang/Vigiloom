namespace Vigiloom.Modules.AlertingOperations.Domain;

internal sealed class OperationAuditRecord
{
    public long RowId { get; private set; }
    public Guid ActorId { get; set; }
    public Guid ResourceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public short Kind { get; set; }
    public string Details { get; set; } = "";
}
