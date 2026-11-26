namespace Vigiloom.Modules.AlertingOperations.Domain;

public sealed class PingTransitionCursor
{
    public long RowId { get; set; }
    public Guid DeviceId { get; set; }
    public required string Target { get; set; }
    public long Generation { get; set; }
}
