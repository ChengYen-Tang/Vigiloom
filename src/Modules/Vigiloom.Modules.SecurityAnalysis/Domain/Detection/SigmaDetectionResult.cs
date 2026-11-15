namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>A matching or indeterminate evaluation of an immutable Agent event.</summary>
public sealed class SigmaDetectionResult
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public Guid MessageId { get; set; }
    public Guid ActivationId { get; set; }
    public Guid TestId { get; set; }
    public required byte[] ActivationVersion { get; set; }
    public int Result { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public int? CorrelationCount { get; set; }
    public DateTimeOffset? CorrelationFirstAt { get; set; }
    public DateTimeOffset? CorrelationLastAt { get; set; }
}
