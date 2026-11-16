using Line.OpenApi.Messaging.Webhook;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Web.Api;

internal static class LineWebhookEndpoints
{
    private const int MaximumBytes = 1024 * 1024;

    public static void MapLineWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/line/webhook", ReceiveAsync).AllowAnonymous()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaximumBytes))
            .Produces(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(503);
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, LineChannel channel, CancellationToken token)
    {
        if (request.ContentLength > MaximumBytes) return Results.StatusCode(413);
        var secret = await channel.GetChannelSecretAsync(token);
        if (secret is null) return Results.StatusCode(503);

        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + read > MaximumBytes) return Results.StatusCode(413);
            body.Write(buffer, 0, read);
        }
        try
        {
            var groups = await LineWebhook.ReadGroupsAsync(secret, body.ToArray(), request.Headers["x-line-signature"].ToString(), token);
            await channel.RecordCandidatesAsync(groups, token);
            return Results.Ok();
        }
        catch (WebhookSignatureException) { return Results.Unauthorized(); }
        catch (WebhookPayloadException) { return Results.BadRequest(); }
    }
}
