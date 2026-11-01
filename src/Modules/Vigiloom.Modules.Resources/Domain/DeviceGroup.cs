namespace Vigiloom.Modules.Resources.Domain;

public sealed class DeviceGroup
{
    private DeviceGroup() { }
    public DeviceGroup(Guid groupId, Guid deviceId)
    {
        if (groupId == Guid.Empty || deviceId == Guid.Empty)
            throw new ArgumentException("Resource identifiers must not be empty.");
        GroupId = groupId;
        DeviceId = deviceId;
    }
    public Guid GroupId { get; private set; }
    public Guid DeviceId { get; private set; }
}
