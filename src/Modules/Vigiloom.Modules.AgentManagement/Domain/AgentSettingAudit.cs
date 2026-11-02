namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Non-secret history of administrator changes to Agent transport settings.</summary>
public sealed class AgentSettingAudit
{
    public long RowId { get; private set; }
    public Guid SettingId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int? HeartbeatSeconds { get; set; }
    public int? SettingsPollSeconds { get; set; }
    public int? BatchIntervalSeconds { get; set; }
    public int EffectiveHeartbeatSeconds { get; set; }
    public int EffectiveSettingsPollSeconds { get; set; }
    public int EffectiveBatchIntervalSeconds { get; set; }
    public string EffectiveRevision { get; set; } = "";
    public byte[] ChangeVersion { get; set; } = [];
}
