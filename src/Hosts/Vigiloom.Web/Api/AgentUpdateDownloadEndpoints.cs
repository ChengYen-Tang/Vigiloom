using System.Security.Cryptography;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentUpdateDownloadEndpoints
{
    public static void MapAgentUpdateDownloadEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/v1/agent/update").RequireAuthorization("Agent")
            .RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance);
        endpoints.MapGet("/targets/{name}", async (string name, HttpContext context, AgentPackageAccess access, ILogger<AgentPackageAccess> logger, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var data = await access.ReadManifestAsync(AgentEndpoints.Bearer(context), name, token);
                return data is null ? Results.NotFound() : Results.File(data, "application/json");
            }
            catch (Exception error) when (Unavailable(error, token))
            {
                logger.LogWarning("Agent manifest unavailable: {ErrorType}", error.GetType().Name);
                return Results.Problem(statusCode: 503, title: "Agent update package is unavailable.");
            }
        }).Produces(200, contentType: "application/json").ProducesProblem(401).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Retrieve the assigned Agent manifest");
        endpoints.MapGet("/packages/{name}", async (string name, HttpContext context, AgentPackageAccess access, ILogger<AgentPackageAccess> logger, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var stream = await access.OpenArchiveAsync(AgentEndpoints.Bearer(context), name, token);
                return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream");
            }
            catch (Exception error) when (Unavailable(error, token))
            {
                logger.LogWarning("Agent package unavailable: {ErrorType}", error.GetType().Name);
                return Results.Problem(statusCode: 503, title: "Agent update package is unavailable.");
            }
        }).Produces(200, contentType: "application/octet-stream").ProducesProblem(401).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Download the authenticated Agent's assigned package");
    }

    private static bool Unavailable(Exception error, CancellationToken token) =>
        error is IOException or HttpRequestException or CryptographicException or TimeoutException or InvalidOperationException ||
        error is OperationCanceledException && !token.IsCancellationRequested;
}
