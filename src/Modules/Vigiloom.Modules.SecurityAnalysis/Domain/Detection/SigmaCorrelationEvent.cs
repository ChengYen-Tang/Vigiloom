namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>An event reference retained for a tested correlation's cross-batch time window.</summary>
public sealed class SigmaCorrelationEvent
{
    public long RowId { get; private set; }
    public Guid TestId { get; set; }
    public required byte[] ActivationVersion { get; set; }
    public Guid AgentId { get; set; }
    public Guid MessageId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
