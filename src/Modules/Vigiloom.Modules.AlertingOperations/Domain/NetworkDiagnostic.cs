namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>Latest transient scanner observation from one Network process.</summary>
internal sealed class NetworkDiagnostic
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset? PingSuccessAt { get; set; }
    public DateTimeOffset? PingFailureAt { get; set; }
    public int PingFailures { get; set; }
    public short PingFailureKind { get; set; }
    public DateTimeOffset? SpeedSuccessAt { get; set; }
    public DateTimeOffset? SpeedFailureAt { get; set; }
    public int SpeedFailures { get; set; }
    public short SpeedFailureKind { get; set; }
}
