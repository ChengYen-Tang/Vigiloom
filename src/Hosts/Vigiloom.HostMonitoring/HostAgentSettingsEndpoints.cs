using System.Net;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostAgentSettingsEndpoints
{
    public static void MapHostAgentSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/settings").RequireAuthorization("Administrator");
        admin.MapGet("/platform", (HostAgentConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token));
        admin.MapPut("/platform", (AgentSettingsUpdate input, HostAgentConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token));
        admin.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ExistsAsync(clients, deviceId, token) ? await ReadAsync(settings, deviceId, context, token) : Results.NotFound());
        admin.MapPut("/devices/{deviceId:guid}", async (Guid deviceId, AgentSettingsUpdate input, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
            await ExistsAsync(clients, deviceId, token) ? await UpdateAsync(settings, deviceId, input, context, token) : Results.NotFound());
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-settings", async (Guid deviceId, HostAgentConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            if (!await ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok((await settings.GetAsync(deviceId, token)).Effective);
        }).RequireAuthorization("MonitoringApiRead");
    }

    private static async Task<bool> ExistsAsync(IHttpClientFactory clients, Guid id, CancellationToken token)
    {
        using var response = await clients.CreateClient("resources").GetAsync($"internal/v1/devices/{id:D}/agent", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
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
