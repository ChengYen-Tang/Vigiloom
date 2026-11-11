namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

/// <summary>Allow-listed, committed inventory policy change.</summary>
public sealed class InventorySettingAudit
{
    public long RowId { get; private set; }
    public Guid SettingId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int? ScanIntervalSeconds { get; set; }
    public int EffectiveScanIntervalSeconds { get; set; }
    public string EffectiveRevision { get; set; } = "";
    public byte[] ChangeVersion { get; set; } = [];
}
