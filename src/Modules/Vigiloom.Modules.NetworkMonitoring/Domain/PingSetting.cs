using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.NetworkMonitoring.Domain;

public sealed class PingSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public string Name { get; set; } = "central";
    public int IntervalSeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 2;
    public int FailureThreshold { get; set; } = 3;
    public int SuccessThreshold { get; set; } = 2;
    public int MaxConcurrency { get; set; } = 64;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; private set; } = [];
}
