namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>Bounded diagnostic counters, not a complete per-request audit trail.</summary>
public sealed class ReceptionRejectionSummary
{
    public long RowId { get; private set; }
    public byte Kind { get; private set; }
    public long ObservedCount { get; private set; }
    public DateTimeOffset? FirstRecordedAt { get; private set; }
    public DateTimeOffset? LastRecordedAt { get; private set; }
    public byte? ExampleIngress { get; private set; }
    public Guid? ExampleSourceId { get; private set; }
}
