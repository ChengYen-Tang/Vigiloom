using Vigiloom.Contracts.Devices;

namespace Vigiloom.Contracts.Resources;

public sealed record DeviceWriteRequest(DeviceInput Input, Guid ActorId);
public sealed record DeviceArchiveRequest(bool Archive, string? Version, Guid ActorId);
public sealed record AddressMatchesRequest(string[] Addresses, Guid? ExcludeId);
