using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteAgentStatus(HttpClient client) : IAgentStatusReader, IAgentOverviewReader, IAgentSettingsStatusReader, IAgentQueueCapacityStatusReader, IHostLogMonitoring, IHostSecurityObservationReader
{
    public async Task<AgentConnectionStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-status", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent status.");
    }

    async Task<AgentSettingsStatus?> IAgentSettingsStatusReader.GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-settings-status", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentSettingsStatus>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent settings status.");
    }

    async Task<AgentQueueCapacityStatus?> IAgentQueueCapacityStatusReader.GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-queue-capacity-status", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentQueueCapacityStatus>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent queue capacity status.");
    }

    async Task<HostLogStatusView?> IHostLogMonitoring.GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-log-status", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostLogStatusView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent log status.");
    }

    async Task<SecurityObservation?> IHostSecurityObservationReader.GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-security-observation", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SecurityObservation>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty security observation.");
    }

    public async Task<IReadOnlyDictionary<Guid, AgentConnectionStatus>> GetConnectionsAsync(IReadOnlyList<Guid> deviceIds, CancellationToken token)
    {
        if (deviceIds.Count == 0) return new Dictionary<Guid, AgentConnectionStatus>();
        using var response = await client.PostAsJsonAsync("internal/v1/agent-connections", deviceIds, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<Guid, AgentConnectionStatus>>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent overview.");
    }
}
