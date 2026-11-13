namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

/// <summary>A change inferred between verified complete observations.</summary>
public sealed class IntegrityChangeRecord
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ScopeId { get; set; }
    public long ScopeRevision { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public int Kind { get; set; }
    public string Key { get; set; } = "";
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public bool CoverageGap { get; set; }
}
