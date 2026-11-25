using Vigiloom.Contracts.Groups;

namespace Vigiloom.Modules.Resources.Application;

public interface IGroupManagement
{
    Task<GroupPage> ListAsync(string? search, int page, CancellationToken token);
    Task<GroupDetails?> GetAsync(Guid id, CancellationToken token);
    Task<DeviceGroupPage> ListForDeviceAsync(Guid deviceId, int page, CancellationToken token);
    Task<GroupDevicePage> ListDevicesAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token);
    Task<GroupResult> CreateAsync(GroupInput input, Guid actor, CancellationToken token);
    Task<GroupResult> UpdateAsync(Guid id, GroupInput input, Guid actor, CancellationToken token);
    Task<GroupResult> ChangeMembersAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token);
    Task<GroupResult> DeleteAsync(Guid id, string? expectedVersion, Guid actor, CancellationToken token);
}
