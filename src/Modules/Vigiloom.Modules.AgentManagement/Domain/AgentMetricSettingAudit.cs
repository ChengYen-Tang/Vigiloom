namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Committed allow-listed metric policy change.</summary>
public sealed class AgentMetricSettingAudit
{
    public long RowId { get; private set; }
    public Guid SettingId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int? HostIntervalSeconds { get; set; }
    public int? ProcessIntervalSeconds { get; set; }
    public int? CpuMillisecondsPerMinute { get; set; }
    public int? WorkingSetLimitMiB { get; set; }
    public int? ProcessCollectionBudgetMs { get; set; }
    public int? IoLimitMiBPerMinute { get; set; }
    public int EffectiveHostIntervalSeconds { get; set; }
    public int EffectiveProcessIntervalSeconds { get; set; }
    public int EffectiveCpuMillisecondsPerMinute { get; set; }
    public int EffectiveWorkingSetLimitMiB { get; set; }
    public int EffectiveProcessCollectionBudgetMs { get; set; }
    public int EffectiveIoLimitMiBPerMinute { get; set; }
    public string EffectiveRevision { get; set; } = "";
    public byte[] ChangeVersion { get; set; } = [];
}
