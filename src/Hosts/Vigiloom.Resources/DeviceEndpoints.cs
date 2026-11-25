using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Resources;

internal static class DeviceEndpoints
{
    public static void MapDeviceManagement(this RouteGroupBuilder api)
    {
        var devices = api.MapGroup("/devices");
        devices.MapGet("/", async (string? search, string? sort, bool? descending, bool? archived, int? page, DeviceManagement service, CancellationToken token) =>
            search?.Length > 256 || page is < 1 or > 42949672 || sort is not (null or "name" or "address" or "created")
                ? Results.BadRequest() : Results.Ok(await service.ListAsync(search, sort, descending ?? false, archived ?? false, page ?? 1, token)));
        devices.MapGet("/{id:guid}", async (Guid id, DeviceManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
        devices.MapPost("/", async (DeviceChangeCommand command, DeviceManagement service, CancellationToken token) =>
            command.ActorId == Guid.Empty || command.Input is null ? Results.BadRequest() :
                Response(await service.CreateAsync(command.Input, command.ActorId, token), created: true));
        devices.MapPut("/{id:guid}", async (Guid id, DeviceChangeCommand command, DeviceManagement service, CancellationToken token) =>
            command.ActorId == Guid.Empty || command.Input is null ? Results.BadRequest() :
                Response(await service.UpdateAsync(id, command.Input, command.ActorId, token)));
        devices.MapPost("/{id:guid}/archive", async (Guid id, DeviceArchiveCommand command, DeviceManagement service, CancellationToken token) =>
            command.ActorId == Guid.Empty ? Results.BadRequest() :
                Response(await service.SetArchivedAsync(id, true, command.Version, command.ActorId, token)));
        devices.MapPost("/{id:guid}/restore", async (Guid id, DeviceArchiveCommand command, DeviceManagement service, CancellationToken token) =>
            command.ActorId == Guid.Empty ? Results.BadRequest() :
                Response(await service.SetArchivedAsync(id, false, command.Version, command.ActorId, token)));
    }

    private static IResult Response(DeviceResult result, bool created = false) => result.Status switch
    {
        DeviceResultStatus.Success when created => Results.Created($"/internal/v1/devices/{result.Device!.Id:D}", result.Device),
        DeviceResultStatus.Success => Results.Ok(result.Device),
        DeviceResultStatus.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(x => x.Key, x => x.Value)),
        DeviceResultStatus.NotFound => Results.NotFound(),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The device has changed or cannot be modified in its current state.",
            extensions: new Dictionary<string, object?> { ["code"] = "device_conflict" })
    };
}
