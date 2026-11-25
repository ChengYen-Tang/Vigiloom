using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Resources;

internal static class InternalGroupEndpoints
{
    public static void MapInternalGroupManagement(this RouteGroupBuilder api)
    {
        var groups = api.MapGroup("/group-management");
        groups.MapGet("/", async (string? search, int page, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(search, page, token)));
        groups.MapGet("/{id:guid}", async (Guid id, GroupManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } group ? Results.Ok(group) : Results.NotFound());
        groups.MapGet("/device/{id:guid}", async (Guid id, int page, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.ListForDeviceAsync(id, page, token)));
        groups.MapGet("/devices", async (Guid? groupId, bool ungrouped, bool? member, string? search, int page, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.ListDevicesAsync(groupId, ungrouped, member, search, page, token)));
        groups.MapPost("/", async (GroupWriteRequest request, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.CreateAsync(request.Input, request.ActorId, token)));
        groups.MapPut("/{id:guid}", async (Guid id, GroupWriteRequest request, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.UpdateAsync(id, request.Input, request.ActorId, token)));
        groups.MapPut("/{id:guid}/devices", async (Guid id, GroupMembersRequest request, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.ChangeMembersAsync(id, request.Change, request.ActorId, token)));
        groups.MapPost("/{id:guid}/delete", async (Guid id, GroupDeleteRequest request, GroupManagement service, CancellationToken token) =>
            Results.Ok(await service.DeleteAsync(id, request.Version, request.ActorId, token)));
    }
}
