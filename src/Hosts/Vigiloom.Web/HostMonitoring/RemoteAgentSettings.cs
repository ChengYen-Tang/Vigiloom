using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteAgentSettings(HttpClient client) : IAgentSettings, IAgentQueueCapacitySettings, IAgentMetricSettings
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

    async Task<AgentQueueCapacityView?> IAgentQueueCapacitySettings.GetAsync(Guid? deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync(CapacityPath(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentQueueCapacityView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned empty queue capacity settings.");
    }

    async Task<AgentQueueCapacityView?> IAgentQueueCapacitySettings.UpdateAsync(Guid? deviceId, AgentQueueCapacityUpdate update, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(CapacityPath(deviceId), new AgentQueueCapacityCommand(update, actorId), token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentQueueCapacityView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty queue capacity update.");
    }

    private static string CapacityPath(Guid? id) => id is { } deviceId
        ? $"internal/v1/agent-queue-capacity/devices/{deviceId:D}" : "internal/v1/agent-queue-capacity/platform";
    async Task<AgentMetricSettingsView?> IAgentMetricSettings.GetAsync(Guid? deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync(MetricPath(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentMetricSettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned empty metric settings.");
    }

    async Task<AgentMetricSettingsView?> IAgentMetricSettings.UpdateAsync(Guid? deviceId, AgentMetricSettingsUpdate update, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(MetricPath(deviceId), new AgentMetricSettingsCommand(update, actorId), token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentMetricSettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty metric settings update.");
    }

    private static string MetricPath(Guid? id) => id is { } deviceId
        ? $"internal/v1/agent-metric-settings/devices/{deviceId:D}" : "internal/v1/agent-metric-settings/platform";
}
