using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentMetricSettingsEndpoints
{
    public static void MapAgentMetricSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/metric-settings")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/platform", (AgentMetricConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token))
            .Produces<AgentMetricSettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapPut("/platform", (AgentMetricSettingsUpdate input, AgentMetricConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token))
            .Produces<AgentMetricSettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        admin.MapGet("/devices/{deviceId:guid}", (Guid deviceId, AgentMetricConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, deviceId, context, token))
            .Produces<AgentMetricSettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPut("/devices/{deviceId:guid}", (Guid deviceId, AgentMetricSettingsUpdate input, AgentMetricConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, deviceId, input, context, token))
            .Produces<AgentMetricSettingsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapGet("/api/v1/agent/metric-settings", async (AgentMetricConfiguration settings, HttpContext context, CancellationToken token) =>
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
            .Produces<AgentMetricSettingsSnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
    }

    private static async Task<IResult> ReadAsync(AgentMetricConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAsync(AgentMetricConfiguration settings, Guid? deviceId, AgentMetricSettingsUpdate input, HttpContext context, CancellationToken token)
    {
        if (!AgentMetricConfiguration.Valid(input?.Overrides)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["interval"] = ["Agent 採集間隔超出允許範圍。"] });
        if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Agent metric settings changed; reload and try again."), allowApiCredential: false);
    }
}
