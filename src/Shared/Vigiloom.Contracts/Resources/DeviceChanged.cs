namespace Vigiloom.Contracts.Resources;

/// <summary>Prompt network monitoring to refresh this device from the resource service.</summary>
public sealed record NetworkDeviceChanged(Guid DeviceId, byte[] Version);

/// <summary>Prompt agent management to refresh this device from the resource service.</summary>
public sealed record AgentDeviceChanged(Guid DeviceId, byte[] Version);
