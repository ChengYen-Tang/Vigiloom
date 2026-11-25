using Vigiloom.Contracts.Groups;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Web.Groups;

internal interface IServiceMembership
{
    Task<ServiceGroupLinkPage> ListGroupsAsync(Guid serviceId, int page, CancellationToken token);
    Task<GroupServicePage> ListAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token);
    Task<GroupResult> ChangeAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token);
}
