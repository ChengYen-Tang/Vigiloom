using Vigiloom.BuildingBlocks.Identifiers;
namespace Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;

public sealed class SpeedSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public string Name { get; set; } = "central";
    public bool Automatic { get; set; } = true;
    public int IntervalHours { get; set; } = 6;
    public int MaxMiB { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 60;
    public DateTimeOffset? NextAutomaticAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; private set; } = [];
}
