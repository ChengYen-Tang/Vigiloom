using Vigiloom.Contracts.Devices;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Resources;

internal static class DeviceEndpoints
{
    public static void MapDeviceManagement(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices");
        devices.MapGet("/", async (string? search, string? sort, bool? descending, bool? archived, int? page, DeviceManagement service, CancellationToken token) =>
            search?.Length > 256 || page is < 1 or > 42949672 || sort is not (null or "name" or "address" or "created")
                ? Results.BadRequest() : Results.Ok(await service.ListAsync(search, sort, descending ?? false, archived ?? false, page ?? 1, token)))
            .RequireAuthorization("MonitoringApiRead");
        devices.MapGet("/{id:guid}", async (Guid id, DeviceManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound())
            .RequireAuthorization("MonitoringApiRead");
        devices.MapGet("/address-matches", async (string address, Guid? excludeId, DeviceManagement service, CancellationToken token) =>
            address.Length > 253 ? Results.BadRequest() : Results.Ok(await service.FindAddressMatchesAsync(address, excludeId, token)))
            .RequireAuthorization("DeviceWrite");
        devices.MapPost("/", async (DeviceInput input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            input.Public && !context.User.IsInRole("Administrator") ? Results.Forbid() :
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.CreateAsync(input, actor, token), created: true), allowApiCredential: !input.Public))
            .RequireAuthorization("DeviceWrite");
        devices.MapPut("/{id:guid}", async (Guid id, DeviceInput input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.UpdateAsync(id, input, actor, token))))
            .RequireAuthorization("DeviceWrite");
        devices.MapPost("/{id:guid}/archive", async (Guid id, DeviceVersionRequest input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.SetArchivedAsync(id, true, input.Version, actor, token))))
            .RequireAuthorization("DeviceWrite");
        devices.MapPost("/{id:guid}/restore", async (Guid id, DeviceVersionRequest input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.SetArchivedAsync(id, false, input.Version, actor, token))))
            .RequireAuthorization("DeviceWrite");
    }

    private static IResult Response(DeviceResult result, bool created = false) => result.Status switch
    {
        DeviceResultStatus.Success when created => Results.Created($"/api/v1/devices/{result.Device!.Id:D}", result.Device),
        DeviceResultStatus.Success => Results.Ok(result.Device),
        DeviceResultStatus.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(x => x.Key, x => x.Value)),
        DeviceResultStatus.NotFound => Results.NotFound(),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The device has changed or cannot be modified in its current state.",
            extensions: new Dictionary<string, object?> { ["code"] = "device_conflict" })
    };
}
