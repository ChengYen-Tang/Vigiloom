using Vigiloom.Contracts.Devices;

namespace Vigiloom.Contracts.Resources;

/// <summary>Device changes made by a previously authorized user.</summary>
public sealed record DeviceChangeCommand(Guid ActorId, DeviceInput? Input);

/// <summary>Archive or restore a device at the version observed by the user.</summary>
public sealed record DeviceArchiveCommand(Guid ActorId, string? Version);
