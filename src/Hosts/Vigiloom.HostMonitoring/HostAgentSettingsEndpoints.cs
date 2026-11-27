using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostAgentSettingsEndpoints
{
    public static void MapHostAgentSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/agent-settings/platform", (HostAgentConfiguration settings, CancellationToken token) => settings.GetAsync(null, token));
        app.MapGet("/internal/v1/agent-settings/devices/{deviceId:guid}", async (Guid deviceId, HostAgentConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? Results.Ok(await settings.GetAsync(deviceId, token)) : Results.NotFound());
        app.MapPost("/internal/v1/agent-settings/platform", (AgentSettingsCommand command, HostAgentConfiguration settings, CancellationToken token) =>
            UpdateInternalAsync(settings, null, command, token));
        app.MapPost("/internal/v1/agent-settings/devices/{deviceId:guid}", async (Guid deviceId, AgentSettingsCommand command, HostAgentConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await UpdateInternalAsync(settings, deviceId, command, token) : Results.NotFound());
        var admin = app.MapGroup("/api/v1/agent-management/settings").RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/platform", (HostAgentConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token));
        admin.MapPut("/platform", (AgentSettingsUpdate input, HostAgentConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token));
        admin.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await ReadAsync(settings, deviceId, context, token) : Results.NotFound());
        admin.MapPut("/devices/{deviceId:guid}", async (Guid deviceId, AgentSettingsUpdate input, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? await UpdateAsync(settings, deviceId, input, context, token) : Results.NotFound());
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-settings", async (Guid deviceId, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok((await settings.GetAsync(deviceId, token)).Effective);
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
    }

    private static async Task<IResult> UpdateInternalAsync(HostAgentConfiguration settings, Guid? deviceId, AgentSettingsCommand command, CancellationToken token)
    {
        if (command.Actor == Guid.Empty || command.Input is null || !AgentConfiguration.Valid(command.Input.Overrides)) return Results.BadRequest();
        var saved = await settings.UpdateAsync(deviceId, command.Input, command.Actor, token);
        return saved is null ? Results.Conflict() : Results.Ok(saved);
    }

    private static async Task<IResult> ReadAsync(HostAgentConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await settings.GetAsync(deviceId, token));
    }

    private static async Task<IResult> UpdateAsync(HostAgentConfiguration settings, Guid? deviceId, AgentSettingsUpdate input, HttpContext context, CancellationToken token)
    {
        if (!AgentConfiguration.Valid(input?.Overrides)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Agent 設定值超出允許範圍。"] });
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Agent settings changed; reload and try again."), allowApiCredential: false);
    }
}
