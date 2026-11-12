namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

public enum IntegrityScopeAction { Created = 1, Updated = 2, Retired = 3 }

/// <summary>Administrative change metadata; paths and exclusion patterns are not duplicated in audit entries.</summary>
public sealed class IntegrityScopeAudit
{
    public long RowId { get; private set; }
    public Guid ScopeId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public long Revision { get; set; }
    public IntegrityScopeAction Action { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
