using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>An open or recovered alert for one configured application health target.</summary>
public sealed class ServiceHealthAlert
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid TargetId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? InstanceId { get; set; }
    public int State { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
}
