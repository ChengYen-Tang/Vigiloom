namespace Vigiloom.Modules.Resources.Domain;

/// <summary>An additional normalized Ping target belonging to a device.</summary>
public sealed class DeviceTarget
{
    private DeviceTarget() { }

    public DeviceTarget(Guid deviceId, string address)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        DeviceId = deviceId;
        Address = address;
    }

    public Guid DeviceId { get; private set; }
    public string Address { get; private set; } = null!;
}
