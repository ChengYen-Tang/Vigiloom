using Vigiloom.Contracts.Devices;

namespace Vigiloom.Modules.Resources.Application;

public interface IDeviceManagement
{
    Task<DevicePage> ListAsync(string? search, string? sort, bool descending, bool archived, int page, CancellationToken token);
    Task<DevicePage> ListForOverviewAsync(int page, CancellationToken token);
    Task<DeviceDetails?> GetAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(string address, Guid? excludeId, CancellationToken token);
    Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(IEnumerable<string> addresses, Guid? excludeId, CancellationToken token);
    Task<DeviceResult> CreateAsync(DeviceInput input, Guid actor, CancellationToken token);
    Task<DeviceResult> UpdateAsync(Guid id, DeviceInput input, Guid actor, CancellationToken token);
    Task<DeviceResult> SetArchivedAsync(Guid id, bool archive, string? expectedVersion, Guid actor, CancellationToken token);
}
