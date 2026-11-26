namespace Vigiloom.Modules.AlertingOperations.Domain;

public sealed class HealthTransitionCursor
{
    public long RowId { get; set; }
    public Guid TargetId { get; set; }
    public long Generation { get; set; }
}
