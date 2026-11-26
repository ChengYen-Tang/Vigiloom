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
        app.MapGet("/api/v1/devices/{id:guid}/agent-status", ReadAsync).RequireAuthorization("MonitoringApiRead")
            .Produces<AgentConnectionStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get Agent connectivity independently of device Ping");
    }

    private static async Task<IResult> ReadAsync(Guid id, IHttpClientFactory clients, HostAgentMonitoring monitoring, HttpContext context, CancellationToken token)
    {
        var resources = clients.CreateClient("resources");
        using var deviceResponse = await resources.GetAsync($"internal/v1/devices/{id:D}/agent", token);
        if (deviceResponse.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
        deviceResponse.EnsureSuccessStatusCode();
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceAgentState>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned an empty device.");
        AgentRegistrationSummary? registration = null;
        if (device.Eligible)
        {
            using var registrationsResponse = await resources.GetAsync($"internal/v1/devices/{id:D}/agents", token);
            if (registrationsResponse.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
            registrationsResponse.EnsureSuccessStatusCode();
            var registrations = await registrationsResponse.Content.ReadFromJsonAsync<DeviceAgentRegistrations>(cancellationToken: token)
                ?? throw new InvalidOperationException("Resources returned empty Agent registrations.");
            registration = registrations.Agents.SingleOrDefault(x => x.RevokedAt is null && x.DeviceGeneration == device.Generation);
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await monitoring.GetAsync(device, registration, token));
    }
}
