namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>A source-attributed indicator observed in a retained host event.</summary>
public sealed class IocDetection
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid MessageId { get; set; }
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public required string Kind { get; set; }
    public required string Value { get; set; }
    public required string Field { get; set; }
    public int SourceLine { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
}
