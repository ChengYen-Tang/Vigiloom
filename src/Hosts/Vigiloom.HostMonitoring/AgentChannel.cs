using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

public sealed class AgentChannel(IHttpClientFactory clients, AgentHeartbeatLimits limits) : Hub
{
    public static string Group(Guid agentId) => $"agent:{agentId:D}";

    public async Task<int> Heartbeat(string credential, AgentHeartbeatReport? report)
    {
        var token = Context.ConnectionAborted;
        var agentId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (agentId is null) return StatusCodes.Status401Unauthorized;
        using var slot = await limits.AcquireSlotAsync(token);
        if (!slot.IsAcquired) return StatusCodes.Status429TooManyRequests;
        using var lease = await limits.AcquireAsync(agentId, token);
        if (!lease.IsAcquired) return StatusCodes.Status429TooManyRequests;
        if (report is not null && JsonSerializer.SerializeToUtf8Bytes(report).Length > AgentHeartbeatEndpoint.MaximumBytes)
            return StatusCodes.Status413PayloadTooLarge;
        if (report is not null && !report.IsValid()) return StatusCodes.Status400BadRequest;
        try
        {
            using var response = await clients.CreateClient("resources").PostAsJsonAsync("internal/v1/agents/authenticate", new AgentCredentialCommand(credential), token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return StatusCodes.Status401Unauthorized;
            response.EnsureSuccessStatusCode();
            var identity = await response.Content.ReadFromJsonAsync<RegisteredAgentIdentity>(cancellationToken: token);
            if (identity is null || identity.AgentId.Version != 7 || identity.DeviceId.Version != 7 || identity.DeviceGeneration < 0 || identity.AgentId.ToString("D") != agentId)
                return StatusCodes.Status401Unauthorized;
            var heartbeat = Context.GetHttpContext()!.RequestServices.GetRequiredService<HostHeartbeat>();
            return await heartbeat.RecordAsync(identity, report, token) switch
            {
                AgentIdentityStatus.Success => StatusCodes.Status204NoContent,
                AgentIdentityStatus.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status401Unauthorized
            };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException && !token.IsCancellationRequested)
        { return StatusCodes.Status503ServiceUnavailable; }
    }

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var agentId))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(agentId));
        await base.OnConnectedAsync();
    }
}
