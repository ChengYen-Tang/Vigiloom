using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostQueueCapacityEndpoints
{
    public static void MapHostQueueCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/agent-queue-capacity/platform", (HostQueueCapacity settings, CancellationToken token) => settings.GetAsync(null, token));
        app.MapGet("/internal/v1/agent-queue-capacity/devices/{deviceId:guid}", async (Guid deviceId, HostQueueCapacity settings, IHttpClientFactory clients, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? Results.Ok(await settings.GetAsync(deviceId, token)) : Results.NotFound());
        app.MapPost("/internal/v1/agent-queue-capacity/platform", (AgentQueueCapacityCommand command, HostQueueCapacity settings, CancellationToken token) =>
            UpdateInternalAsync(settings, null, command, token));
        app.MapPost("/internal/v1/agent-queue-capacity/devices/{deviceId:guid}", async (Guid deviceId, AgentQueueCapacityCommand command, HostQueueCapacity settings, IHttpClientFactory clients, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await UpdateInternalAsync(settings, deviceId, command, token) : Results.NotFound());
        var admin = app.MapGroup("/api/v1/agent-management/queue-capacity").RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/platform", (HostQueueCapacity settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token));
        admin.MapPut("/platform", (AgentQueueCapacityUpdate input, HostQueueCapacity settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token));
        admin.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HostQueueCapacity settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await ReadAsync(settings, deviceId, context, token) : Results.NotFound());
        admin.MapPut("/devices/{deviceId:guid}", async (Guid deviceId, AgentQueueCapacityUpdate input, HostQueueCapacity settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await UpdateAsync(settings, deviceId, input, context, token) : Results.NotFound());
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-queue-capacity", async (Guid deviceId, HostQueueCapacity settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok((await settings.GetAsync(deviceId, token)).Effective);
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        app.MapGet("/api/v1/agent/queue-capacity", async (HttpContext context, HostQueueCapacity settings, CancellationToken token) =>
        {
            var snapshot = await settings.GetForAgentAsync((RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!, token);
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            var requested = context.Request.GetTypedHeaders().IfNoneMatch;
            return requested?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true
                ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(snapshot);
        });
    }

    private static async Task<IResult> ReadAsync(HostQueueCapacity settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await settings.GetAsync(deviceId, token));
    }

    private static async Task<IResult> UpdateAsync(HostQueueCapacity settings, Guid? deviceId, AgentQueueCapacityUpdate input, HttpContext context, CancellationToken token)
    {
        if (!AgentQueueCapacityConfiguration.Valid(input?.Overrides)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["capacity"] = ["Agent 佇列容量超出允許範圍。"] });
        return await ApiMutation.ExecuteAsync(context, async actor => await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved
            ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Agent queue capacity changed; reload and try again."), allowApiCredential: false);
    }

    private static async Task<IResult> UpdateInternalAsync(HostQueueCapacity settings, Guid? deviceId, AgentQueueCapacityCommand command, CancellationToken token)
    {
        if (command.Actor == Guid.Empty || command.Input is null || !AgentQueueCapacityConfiguration.Valid(command.Input.Overrides)) return Results.BadRequest();
        var saved = await settings.UpdateAsync(deviceId, command.Input, command.Actor, token);
        return saved is null ? Results.Conflict() : Results.Ok(saved);
    }
}
