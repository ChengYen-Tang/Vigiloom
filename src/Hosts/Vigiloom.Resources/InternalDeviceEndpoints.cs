using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Resources;

internal static class InternalDeviceEndpoints
{
    public static void MapInternalDeviceManagement(this RouteGroupBuilder api)
    {
        var devices = api.MapGroup("/device-management");
        devices.MapGet("/", async (string? search, string? sort, bool descending, bool archived, int page, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(search, sort, descending, archived, page, token)));
        devices.MapGet("/overview", async (int page, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.ListForOverviewAsync(page, token)));
        devices.MapGet("/{id:guid}", async (Guid id, DeviceManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
        devices.MapPost("/address-matches", async (AddressMatchesRequest request, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.FindAddressMatchesAsync(request.Addresses, request.ExcludeId, token)));
        devices.MapPost("/", async (DeviceWriteRequest request, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.CreateAsync(request.Input, request.ActorId, token)));
        devices.MapPut("/{id:guid}", async (Guid id, DeviceWriteRequest request, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.UpdateAsync(id, request.Input, request.ActorId, token)));
        devices.MapPost("/{id:guid}/archive", async (Guid id, DeviceArchiveRequest request, DeviceManagement service, CancellationToken token) =>
            Results.Ok(await service.SetArchivedAsync(id, request.Archive, request.Version, request.ActorId, token)));
    }
}
