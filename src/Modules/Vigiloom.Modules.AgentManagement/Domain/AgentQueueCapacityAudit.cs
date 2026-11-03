namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Allow-listed values for one committed capacity-setting change.</summary>
public sealed class AgentQueueCapacityAudit
{
    public long RowId { get; private set; }
    public Guid SettingId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long? PayloadBytes { get; set; }
    public int? BatchCount { get; set; }
    public long EffectivePayloadBytes { get; set; }
    public int EffectiveBatchCount { get; set; }
    public string EffectiveRevision { get; set; } = "";
    public byte[] ChangeVersion { get; set; } = [];
}
