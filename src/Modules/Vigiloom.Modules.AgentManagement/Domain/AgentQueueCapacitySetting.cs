using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>One capacity override layer; a null device represents platform defaults.</summary>
public sealed class AgentQueueCapacitySetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid? DeviceId { get; set; }
    public long? PayloadBytes { get; set; }
    public int? BatchCount { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}
