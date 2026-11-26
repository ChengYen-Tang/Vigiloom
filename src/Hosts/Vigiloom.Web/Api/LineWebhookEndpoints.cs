using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Web.Api;

internal static class LineWebhookEndpoints
{
    public static void MapLineWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/line/webhook", ReceiveAsync).AllowAnonymous()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LineWebhookReceiver.MaximumBytes))
            .Produces(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(503);
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, Vigiloom.Web.Operations.LineManagement channel, CancellationToken token)
    {
        return Results.StatusCode(await channel.ReceiveAsync(request, token));
    }
}
