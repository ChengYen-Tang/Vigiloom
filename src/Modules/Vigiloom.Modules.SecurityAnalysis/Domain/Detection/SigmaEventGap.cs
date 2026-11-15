namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>Records an event that could not be fully evaluated.</summary>
public sealed class SigmaEventGap
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>1: original event missing; 2: at least one enabled rule unavailable.</summary>
    public int Kind { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
