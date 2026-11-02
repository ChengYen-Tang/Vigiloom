using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentConfigurationEndpoints
{
    public static void MapAgentConfigurationEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/settings")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/platform", (AgentConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token))
            .Produces<AgentSettingsView>().ProducesProblem(401).ProducesProblem(403).WithSummary("Read platform Agent scheduling settings");
        admin.MapPut("/platform", (AgentSettingsUpdate input, AgentConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token))
            .Produces<AgentSettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).WithSummary("Update platform Agent scheduling settings");
        admin.MapGet("/devices/{deviceId:guid}", (Guid deviceId, AgentConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, deviceId, context, token))
            .Produces<AgentSettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).WithSummary("Read device Agent scheduling overrides");
        admin.MapPut("/devices/{deviceId:guid}", (Guid deviceId, AgentSettingsUpdate input, AgentConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, deviceId, input, context, token))
            .Produces<AgentSettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).WithSummary("Update device Agent scheduling overrides");

        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-settings", async (Guid deviceId, AgentConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view.Effective) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read effective Agent scheduling settings for a device");

        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-settings-status", async (Guid deviceId, AgentSettingsMonitoring monitoring, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } status ? Results.Ok(status) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentSettingsStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read the Agent's observed scheduling configuration status");

        app.MapGet("/api/v1/agent/settings", async (AgentConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            var snapshot = await settings.GetForAgentAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            var requested = context.Request.GetTypedHeaders().IfNoneMatch;
            if (requested?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<AgentSettingsSnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read effective settings for the authenticated Agent");
    }

    private static async Task<IResult> ReadAsync(AgentConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAsync(AgentConfiguration settings, Guid? deviceId, AgentSettingsUpdate input, HttpContext context, CancellationToken token)
    {
        if (!AgentConfiguration.Valid(input?.Overrides)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Agent 設定值超出允許範圍。"] });
        if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Agent settings changed; reload and try again."), allowApiCredential: false);
    }
}
