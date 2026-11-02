using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Explicit platform or device transport overrides; null fields inherit the preceding layer.</summary>
public sealed class AgentSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid? DeviceId { get; set; }
    public int? HeartbeatSeconds { get; set; }
    public int? SettingsPollSeconds { get; set; }
    public int? BatchIntervalSeconds { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}
