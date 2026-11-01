namespace Vigiloom.Modules.Resources.Domain;

/// <summary>An immutable audit record for a device management action.</summary>
public sealed class DeviceAudit
{
    private DeviceAudit() { }
    public DeviceAudit(Guid deviceId, Guid actorId, string action, DateTimeOffset occurredAt)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty) throw new ArgumentException("Device and actor IDs are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        DeviceId = deviceId;
        ActorId = actorId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
    }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
