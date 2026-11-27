using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Operations;

internal static class PublicLineSettingsEndpoints
{
    internal static void MapPublicLineSettings(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/line").RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/", async (HttpContext context, LineChannel channel, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await channel.GetAsync(token));
        }).Produces<LineChannelView>().ProducesProblem(401).ProducesProblem(403);
        admin.MapPut("/", async (LineChannelUpdate update, HttpContext context, LineChannel channel, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => await channel.SaveAsync(update, actor, token)
                ? Results.NoContent() : Results.BadRequest(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        admin.MapPut("/groups/{groupId:guid}", async (Guid groupId, LineGroupUpdate update, HttpContext context, LineChannel channel, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor => await channel.SetGroupAsync(groupId, update.Name, update.Approved, actor, token)
                ? Results.NoContent() : Results.BadRequest(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        admin.MapPost("/groups/{groupId:guid}/test", async (Guid groupId, HttpContext context, LineDeliveryQueue queue, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ => await queue.EnqueueTestAsync(groupId, token) is { } id
                ? Results.Accepted(value: new LineTestResponse(id)) : Results.BadRequest(), allowApiCredential: false))
            .Produces<LineTestResponse>(202).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        admin.MapGet("/deliveries", async (HttpContext context, LineDeliveryStatus deliveries, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await deliveries.GetRecentAsync(token));
        }).Produces<IReadOnlyList<LineDeliverySummary>>().ProducesProblem(401).ProducesProblem(403);
        admin.MapGet("/deliveries/{id:guid}", async (Guid id, HttpContext context, LineDeliveryStatus deliveries, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await deliveries.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<LineDeliveryView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }

    private sealed record LineGroupUpdate(string? Name, [property: System.Text.Json.Serialization.JsonRequired] bool Approved);
    private sealed record LineTestResponse(Guid Id);
}
