using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>A single outage of one monitored target.</summary>
public sealed class PingAlert
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public required string Target { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
}
