namespace Vigiloom.Modules.AlertingOperations.Domain;

// Fixed internal slots, not independently addressable resources or historical events.
internal sealed class WorkerDiagnosticSlot
{
    public int Slot { get; set; }
    public Guid? BootId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? PingSuccessAt { get; set; }
    public DateTimeOffset? PingFailureAt { get; set; }
    public int PingFailures { get; set; }
    public short PingFailureKind { get; set; }
    public DateTimeOffset? SpeedSuccessAt { get; set; }
    public DateTimeOffset? SpeedFailureAt { get; set; }
    public int SpeedFailures { get; set; }
    public short SpeedFailureKind { get; set; }
}
