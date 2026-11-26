using Vigiloom.Contracts.Resources;

namespace Vigiloom.Contracts.Network;

public interface IPingStatusReader
{
    Task<DevicePingStatus> GetStatusAsync(Guid id, IReadOnlyList<string> targets, string deviceVersion, CancellationToken token);
    Task<IReadOnlyDictionary<Guid, DevicePingStatus>> GetStatusesAsync(IReadOnlyList<PingDeviceSnapshot> devices, CancellationToken token);
}

public sealed record PingStatusRequest(Guid DeviceId, IReadOnlyList<string> Targets, string DeviceVersion);
