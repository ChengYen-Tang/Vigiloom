using System.Buffers;
using System.Security.Cryptography;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentBatchEndpoints
{
    public static void MapAgentBatchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/agent/batches", ReceiveAsync)
            .RequireAuthorization("Agent").RequireRateLimiting("agent-transport")
            .WithMetadata(AgentTransportEndpoint.Instance, new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AgentBatchCodec.MaximumBytes))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Commit an authenticated, validated Agent runtime batch");
    }

    private static async Task<IResult> ReceiveAsync(HttpContext context, AgentBatchReceiver receiver, CancellationToken token)
    {
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > AgentBatchCodec.MaximumBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var body = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, token)) != 0)
            {
                if (body.Length + read > AgentBatchCodec.MaximumBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                await body.WriteAsync(chunk.AsMemory(0, read), token);
            }
            var result = await receiver.ReceiveAsync(AgentEndpoints.Bearer(context), body.GetBuffer().AsMemory(0, (int)body.Length), token);
            return result switch
            {
                BatchReceiptStatus.Accepted or BatchReceiptStatus.AlreadyAccepted => Results.NoContent(),
                BatchReceiptStatus.Unauthorized => Results.Unauthorized(),
                BatchReceiptStatus.Invalid => Results.BadRequest(),
                BatchReceiptStatus.Conflict => Results.Conflict(),
                BatchReceiptStatus.CapacityExceeded => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body.GetBuffer().AsSpan(0, (int)body.Length));
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }
}
