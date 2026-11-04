using Vigiloom.Contracts.Groups;
using Microsoft.AspNetCore.Mvc;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Web.Groups;

namespace Vigiloom.Web.Api;

internal static class GroupEndpoints
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        var groups = app.MapGroup("/api/v1/groups").RequireRateLimiting("api-client");
        groups.ProducesProblem(429);
        groups.MapGet("/", async (string? search, int? page, GroupManagement service, CancellationToken token) =>
            InvalidSearch(search, page) ? Results.BadRequest() : Results.Ok(await service.ListAsync(search, page ?? 1, token)))
            .RequireAuthorization("MonitoringApiRead").Produces<GroupPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List groups");
        groups.MapGet("/ungrouped-devices", async (string? search, int? page, GroupManagement service, CancellationToken token) =>
            InvalidSearch(search, page) ? Results.BadRequest() : Results.Ok(await service.ListDevicesAsync(null, true, null, search, page ?? 1, token)))
            .RequireAuthorization("MonitoringApiRead").Produces<GroupDevicePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List devices without a group");
        groups.MapGet("/{id:guid}", async (Guid id, GroupManagement service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization("MonitoringApiRead").Produces<GroupDetails>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get a group");
        groups.MapGet("/{id:guid}/devices", async (Guid id, bool? member, string? search, int? page, GroupManagement service, CancellationToken token) =>
        {
            if (InvalidSearch(search, page)) return Results.BadRequest();
            if (await service.GetAsync(id, token) is null) return Results.NotFound();
            return Results.Ok(await service.ListDevicesAsync(id, false, member, search, page ?? 1, token));
        }).RequireAuthorization("MonitoringApiRead").Produces<GroupDevicePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List group devices or candidates");
        groups.MapGet("/{id:guid}/services", async (Guid id, bool? member, string? search, int? page, GroupManagement groupsService, ServiceMembership service, CancellationToken token) =>
        {
            if (InvalidSearch(search, page)) return Results.BadRequest();
            if (await groupsService.GetAsync(id, token) is null) return Results.NotFound();
            return Results.Ok(await service.ListAsync(id, false, member, search, page ?? 1, token));
        }).RequireAuthorization("MonitoringApiRead").Produces<GroupServicePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List group services or candidates");
        groups.MapGet("/ungrouped-services", async (string? search, int? page, ServiceMembership service, CancellationToken token) =>
            InvalidSearch(search, page) ? Results.BadRequest() : Results.Ok(await service.ListAsync(null, true, null, search, page ?? 1, token)))
            .RequireAuthorization("MonitoringApiRead").Produces<GroupServicePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List services without a group");
        groups.MapPost("/", async (GroupInput input, HttpContext context, GroupManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.CreateAsync(input, actor, token), created: true)))
            .RequireAuthorization("DeviceWrite").Produces<GroupDetails>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Create a group");
        groups.MapPut("/{id:guid}", async (Guid id, GroupInput input, HttpContext context, GroupManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.UpdateAsync(id, input, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<GroupDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Update a group");
        groups.MapPut("/{id:guid}/devices", async (Guid id, GroupMembershipChange input, HttpContext context, GroupManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.ChangeMembersAsync(id, input, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<GroupDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Change device memberships");
        groups.MapPut("/{id:guid}/services", async (Guid id, GroupMembershipChange input, HttpContext context, ServiceMembership service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.ChangeAsync(id, input, actor, token))))
            .RequireAuthorization("DeviceWrite").Produces<GroupDetails>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Change service memberships");
        groups.MapDelete("/{id:guid}", async (Guid id, [FromBody] GroupVersionRequest input, HttpContext context, GroupManagement service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => Response(await service.DeleteAsync(id, input.Version, actor, token), deleted: true)))
            .RequireAuthorization("DeviceWrite").Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Delete a group without deleting its members");
    }

    private static bool InvalidSearch(string? search, int? page) => search?.Length > 256 || page is < 1 or > 42949672;

    private static IResult Response(GroupResult result, bool created = false, bool deleted = false) => result.Status switch
    {
        GroupResultStatus.Success when deleted => Results.NoContent(),
        GroupResultStatus.Success when created => Results.Created($"/api/v1/groups/{result.Group!.Id:D}", result.Group),
        GroupResultStatus.Success => Results.Ok(result.Group),
        GroupResultStatus.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(x => x.Key, x => x.Value)),
        GroupResultStatus.NotFound => Results.NotFound(),
        _ => Results.Problem(statusCode: 409, title: "The group has changed; reload and try again.",
            extensions: new Dictionary<string, object?> { ["code"] = "group_conflict" })
    };
}
