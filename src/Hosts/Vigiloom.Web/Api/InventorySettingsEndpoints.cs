using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class InventorySettingsEndpoints
{
    public static void MapInventorySettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/inventory-settings-status", async (Guid deviceId, AgentInventorySettingsMonitoring monitoring, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } status ? Results.Ok(status) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<InventorySettingsStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        app.MapGet("/api/v1/agent/inventory-settings", async (int? contract, AgentInventorySettings settings, HttpContext context, CancellationToken token) =>
        {
            if (contract is not (null or 2)) return Results.BadRequest();
            var snapshot = await settings.GetAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            if (contract is null && snapshot.Settings.CollectCommandLine)
                snapshot = InventorySettingsSnapshot.Create(new(snapshot.Settings.ScanIntervalSeconds));
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            var requested = context.Request.GetTypedHeaders().IfNoneMatch;
            if (requested?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<InventorySettingsSnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
    }

}
