using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices").RequireRateLimiting("api-client");
        devices.MapGet("/", async (string? search, string? sort, bool? descending, bool? archived, int? page, DeviceManagement service, CancellationToken token) =>
        {
            if (search?.Length > 256 || page is < 1 or > 42949672 || sort is not (null or "name" or "address" or "created")) return Results.BadRequest();
            return Results.Ok(await service.ListAsync(search, sort, descending ?? false, archived ?? false, page ?? 1, token));
        }).RequireAuthorization("DeviceRead").Produces<DevicePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List monitored devices");
        devices.MapGet("/{id:guid}", async (Guid id, DeviceManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound())
            .RequireAuthorization("DeviceRead").Produces<DeviceDetails>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithName("GetDevice").WithSummary("Get a device");
        devices.MapGet("/address-matches", async (string address, Guid? excludeId, DeviceManagement service, CancellationToken token) =>
            address.Length > 253 ? Results.BadRequest() : Results.Ok(await service.FindAddressMatchesAsync(address, excludeId, token)))
            .RequireAuthorization("DeviceWrite").Produces<IReadOnlyList<DeviceDetails>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Find devices using a target address");
        devices.MapPost("/", async (DeviceInput input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await Write(context, async actor => Response(await service.CreateAsync(input, actor, token), created: true)))
            .RequireAuthorization("DeviceWrite").Produces<DeviceDetails>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Create a private device");
        devices.MapPut("/{id:guid}", async (Guid id, DeviceInput input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await Write(context, async actor => Response(await service.UpdateAsync(id, input, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<DeviceDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Update a device");
        devices.MapPost("/{id:guid}/archive", async (Guid id, DeviceVersionRequest input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await Write(context, async actor => Response(await service.SetArchivedAsync(id, true, input.Version, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<DeviceDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Archive a device");
        devices.MapPost("/{id:guid}/restore", async (Guid id, DeviceVersionRequest input, HttpContext context, DeviceManagement service, CancellationToken token) =>
            await Write(context, async actor => Response(await service.SetArchivedAsync(id, false, input.Version, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<DeviceDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Restore a device in the disabled state");
    }

    private static async Task<IResult> Write(HttpContext context, Func<Guid, Task<IResult>> operation)
    {
        var apiIdentity = context.User.Identities.FirstOrDefault(identity => identity.AuthenticationType == ApiCredentialHandler.SchemeName && identity.HasClaim("scope", "monitoring.write"));
        if (apiIdentity is null)
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        }
        var id = apiIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(id, out var actor) && actor != Guid.Empty ? await operation(actor) : Results.Forbid();
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

/// <summary>The rowversion returned by the most recent read.</summary>
public sealed record DeviceVersionRequest(string? Version);
