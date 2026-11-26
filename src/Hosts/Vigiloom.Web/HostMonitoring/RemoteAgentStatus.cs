using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Devices;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteAgentStatus(HttpClient client) : IAgentStatusReader
{
    public async Task<AgentConnectionStatus?> GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-status", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent status.");
    }
}
