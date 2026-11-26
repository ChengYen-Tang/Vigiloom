using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostAgentStatusEndpoints
{
    public static void MapHostAgentStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/devices/{id:guid}/agent-status", ReadAsync);
        app.MapGet("/internal/v1/devices/{id:guid}/agent-settings-status", ReadSettingsAsync);
        app.MapGet("/internal/v1/devices/{id:guid}/agent-queue-capacity-status", ReadCapacityAsync);
        app.MapGet("/internal/v1/devices/{id:guid}/agent-log-status", async (Guid id, IHttpClientFactory clients, HostAgentMonitoring monitoring, CancellationToken token) =>
        {
            var overview = await ReadDeviceAsync(id, clients, token);
            if (overview is null) return Results.NotFound();
            var status = await monitoring.GetLogStatusAsync(overview.Device, overview.Registration, token);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });
        app.MapPost("/internal/v1/agent-connections", async (Guid[] ids, IHttpClientFactory clients, HostAgentMonitoring monitoring, CancellationToken token) =>
        {
            using var response = await clients.CreateClient("resources").PostAsJsonAsync("internal/v1/devices/agent-overview", ids, token);
            response.EnsureSuccessStatusCode();
            var devices = await response.Content.ReadFromJsonAsync<DeviceAgentOverview[]>(cancellationToken: token)
                ?? throw new InvalidOperationException("Resources returned an empty Agent overview.");
            return Results.Ok(await monitoring.GetConnectionsAsync(devices, token));
        });
        app.MapGet("/api/v1/devices/{id:guid}/agent-status", ReadAsync).RequireAuthorization("MonitoringApiRead")
            .Produces<AgentConnectionStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get Agent connectivity independently of device Ping");
        app.MapGet("/api/v1/devices/{id:guid}/agent-settings-status", ReadSettingsAsync).RequireAuthorization("MonitoringApiRead")
            .Produces<AgentSettingsStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get the Agent's observed scheduling settings");
        app.MapGet("/api/v1/devices/{id:guid}/agent-queue-capacity-status", ReadCapacityAsync).RequireAuthorization("MonitoringApiRead")
            .Produces<AgentQueueCapacityStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get the Agent's observed queue capacity");
    }

    private static async Task<IResult> ReadAsync(Guid id, IHttpClientFactory clients, HostAgentMonitoring monitoring, HttpContext context, CancellationToken token)
    {
        var overview = await ReadDeviceAsync(id, clients, token);
        if (overview is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await monitoring.GetAsync(overview.Device, overview.Registration, token));
    }

    private static async Task<IResult> ReadSettingsAsync(Guid id, IHttpClientFactory clients, HostAgentMonitoring monitoring, HttpContext context, CancellationToken token)
    {
        var overview = await ReadDeviceAsync(id, clients, token);
        if (overview is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await monitoring.GetSettingsStatusAsync(overview.Device, overview.Registration, token));
    }

    private static async Task<IResult> ReadCapacityAsync(Guid id, IHttpClientFactory clients, HostAgentMonitoring monitoring, HttpContext context, CancellationToken token)
    {
        var overview = await ReadDeviceAsync(id, clients, token);
        if (overview is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await monitoring.GetQueueCapacityStatusAsync(overview.Device, overview.Registration, token));
    }

    private static async Task<DeviceAgentOverview?> ReadDeviceAsync(Guid id, IHttpClientFactory clients, CancellationToken token)
    {
        var resources = clients.CreateClient("resources");
        using var deviceResponse = await resources.GetAsync($"internal/v1/devices/{id:D}/agent", token);
        if (deviceResponse.StatusCode == HttpStatusCode.NotFound) return null;
        deviceResponse.EnsureSuccessStatusCode();
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceAgentState>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned an empty device.");
        AgentRegistrationSummary? registration = null;
        if (device.Eligible)
        {
            using var registrationsResponse = await resources.GetAsync($"internal/v1/devices/{id:D}/agents", token);
            if (registrationsResponse.StatusCode == HttpStatusCode.NotFound) return null;
            registrationsResponse.EnsureSuccessStatusCode();
            var registrations = await registrationsResponse.Content.ReadFromJsonAsync<DeviceAgentRegistrations>(cancellationToken: token)
                ?? throw new InvalidOperationException("Resources returned empty Agent registrations.");
            registration = registrations.Agents.SingleOrDefault(x => x.RevokedAt is null && x.DeviceGeneration == device.Generation);
        }
        return new DeviceAgentOverview(device, registration);
    }
}
