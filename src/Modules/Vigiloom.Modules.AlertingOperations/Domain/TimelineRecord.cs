namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A received timeline summary; original event details remain at their source.</summary>
public sealed class TimelineRecord
{
    public long RowId { get; set; }
    public Guid MessageId { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid ResourceId { get; set; }
    public required string ResourceKind { get; set; }
    public required string Summary { get; set; }
    public required string Evidence { get; set; }
    public required string Link { get; set; }
    public string? RuleLink { get; set; }
}
