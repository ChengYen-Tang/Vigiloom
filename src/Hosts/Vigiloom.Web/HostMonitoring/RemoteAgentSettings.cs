using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteAgentSettings(HttpClient client) : IAgentSettings
{
    public async Task<AgentSettingsView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync(Path(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentSettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned empty Agent settings.");
    }

    public async Task<AgentSettingsView?> UpdateAsync(Guid? deviceId, AgentSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(Path(deviceId), new AgentSettingsCommand(update, actorId), token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentSettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty Agent settings update.");
    }

    private static string Path(Guid? id) => id is { } deviceId
        ? $"internal/v1/agent-settings/devices/{deviceId:D}" : "internal/v1/agent-settings/platform";
}
