using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>One platform or device override layer for metric collection.</summary>
public sealed class AgentMetricSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid? DeviceId { get; set; }
    public int? HostIntervalSeconds { get; set; }
    public int? ProcessIntervalSeconds { get; set; }
    public int? CpuMillisecondsPerMinute { get; set; }
    public int? WorkingSetLimitMiB { get; set; }
    public int? ProcessCollectionBudgetMs { get; set; }
    public int? IoLimitMiBPerMinute { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}
