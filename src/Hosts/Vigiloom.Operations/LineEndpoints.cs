using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Operations;

internal static class LineEndpoints
{
    internal static void MapLineEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/line/webhook", async (HttpRequest request, LineChannel channel, CancellationToken token) =>
            Results.StatusCode(await new LineWebhookReceiver(channel).ReceiveAsync(request.Body, request.ContentLength,
                request.Headers["x-line-signature"].ToString(), token)))
            .AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LineWebhookReceiver.MaximumBytes))
            .Produces(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(503);
        var line = app.MapGroup("/internal/v1/line");
        line.MapGet("/", async (HttpContext context, LineChannel channel, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var view = await channel.GetAsync(token);
            return Results.Ok(new LineChannelInfo(view.Enabled, view.HasAccessToken, view.HasChannelSecret,
                view.Groups.Select(group => new LineGroupInfo(group.Id, group.ExternalId, group.Name, group.Approved, group.LastSeenAt)).ToArray(), view.AggregationSeconds));
        });
        line.MapPut("/", async (LineChannelChange input, LineChannel channel, CancellationToken token) =>
            await channel.SaveAsync(new(input.Enabled, input.AccessToken, input.ChannelSecret, input.AggregationSeconds), input.ActorId, token)
                ? Results.NoContent() : Results.BadRequest());
        line.MapPut("/groups/{id:guid}", async (Guid id, LineGroupChange input, LineChannel channel, CancellationToken token) =>
            await channel.SetGroupAsync(id, input.Name, input.Approved, input.ActorId, token)
                ? Results.NoContent() : Results.BadRequest());
        line.MapPost("/groups/{id:guid}/test", async (Guid id, LineDeliveryQueue queue, CancellationToken token) =>
            await queue.EnqueueTestAsync(id, token) is { } delivery
                ? Results.Accepted(value: new LineTestDelivery(delivery)) : Results.BadRequest());
        line.MapGet("/deliveries", async (HttpContext context, LineDeliveryStatus deliveries, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var recent = await deliveries.GetRecentAsync(token);
            return Results.Ok(recent.Select(item => new LineDeliveryInfoSummary(item.Id, item.GroupName, item.Status, item.FailureCode, item.CreatedAt)).ToArray());
        });
        line.MapGet("/deliveries/{id:guid}", async (Guid id, HttpContext context, LineDeliveryStatus deliveries, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await deliveries.GetAsync(id, token) is { } result
                ? Results.Ok(new LineDeliveryInfo(result.Id, result.Status, result.FailureCode, result.CreatedAt, result.CompletedAt)) : Results.NotFound();
        });
    }
}
