namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>An administrator-requested, single-file Agent scan and its result.</summary>
public sealed class YaraScan
{
    public Guid Id { get; set; }
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid RequestedBy { get; set; }
    public required string FilePath { get; set; }
    public required string Rules { get; set; }
    public long MaximumBytes { get; set; }
    public int TimeoutSeconds { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public int? Outcome { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public bool? OutputTruncated { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
