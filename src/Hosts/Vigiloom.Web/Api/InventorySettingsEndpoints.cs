using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class InventorySettingsEndpoints
{
    public static void MapInventorySettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/inventory-settings")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/platform", (InventoryConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapPut("/platform", (InventorySettingsUpdate input, InventoryConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        admin.MapGet("/devices/{deviceId:guid}", (Guid deviceId, InventoryConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, deviceId, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPut("/devices/{deviceId:guid}", (Guid deviceId, InventorySettingsUpdate input, InventoryConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, deviceId, input, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

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

    private static async Task<IResult> ReadAsync(InventoryConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAsync(InventoryConfiguration settings, Guid? deviceId, InventorySettingsUpdate input, HttpContext context, CancellationToken token)
    {
        if (!InventoryConfiguration.Valid(input?.Overrides))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["interval"] = ["盤點間隔超出允許範圍。"] });
        if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) :
                Results.Problem(statusCode: 409, title: "Inventory settings changed; reload and try again."), allowApiCredential: false);
    }
}
