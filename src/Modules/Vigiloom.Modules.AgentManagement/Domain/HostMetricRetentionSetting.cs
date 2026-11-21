using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class HostMetricRetentionSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public int Slot { get; set; } = 1;
    public int RawDays { get; set; } = 30;
    public int RollupDays { get; set; } = 365;
    public byte[] Version { get; private set; } = [];
}
