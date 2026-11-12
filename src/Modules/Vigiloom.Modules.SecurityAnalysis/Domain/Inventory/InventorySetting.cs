using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

/// <summary>One platform or device inventory policy override.</summary>
public sealed class InventorySetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid? DeviceId { get; set; }
    public int? ScanIntervalSeconds { get; set; }
    public bool? CollectCommandLine { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}
