using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

public sealed class AuditRetentionSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public int Slot { get; set; } = 1;
    public int Days { get; set; } = 365;
    public byte[] Version { get; private set; } = [];
}
