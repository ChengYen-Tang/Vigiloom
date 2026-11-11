using System.Globalization;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentUpdateMetadataEndpoints
{
    public static void MapAgentUpdateMetadataEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/update/metadata/{name}", async (string name, HttpContext context, UpdateTrustStore store, ILogger<UpdateTrustStore> logger, CancellationToken token) =>
        {
            if (!TryParseName(name, out var role, out var version)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var data = await store.ReadMetadataAsync(role, version, token);
                return data is null ? Results.NotFound() : Results.File(data, "application/json");
            }
            catch (InvalidDataException error)
            {
                logger.LogError(error, "Stored Agent update trust metadata is unavailable or corrupt.");
                return Results.Problem(statusCode: 503, title: "Agent update metadata is unavailable.");
            }
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces(200, contentType: "application/json").ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Retrieve publisher-signed update metadata for a registered Agent");
    }

    internal static bool TryParseName(string name, out string role, out int? version)
    {
        role = "";
        version = null;
        var parts = name.Split('.');
        if (parts.Length == 2 && parts[1] == "json" && parts[0] is "timestamp" or "snapshot" or "targets")
        { role = parts[0]; return true; }
        if (parts.Length != 3 || parts[2] != "json" || parts[1] is not ("root" or "snapshot" or "targets") ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
            number.ToString(CultureInfo.InvariantCulture) != parts[0]) return false;
        role = parts[1]; version = number; return true;
    }
}
