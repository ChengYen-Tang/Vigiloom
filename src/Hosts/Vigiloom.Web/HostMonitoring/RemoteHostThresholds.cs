using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteHostThresholds(HttpClient client) : IHostMetricThresholds
{
    public async Task<HostMetricThresholdStatus?> GetAsync(Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/host-threshold/query", new HostMetricThresholdQuery(deviceId, agentId, kind, source, dimension), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid metric series.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostMetricThresholdStatus>(cancellationToken: token) ?? throw new InvalidOperationException("Empty threshold response.");
    }

    public async Task<HostMetricThresholdView?> UpdateAsync(Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension, HostMetricThresholdInput input, Guid actorId, CancellationToken token)
    {
        var query = new HostMetricThresholdQuery(deviceId, agentId, kind, source, dimension);
        using var response = await client.PostAsJsonAsync("internal/v1/host-threshold/update", new HostMetricThresholdCommand(query, input, actorId), token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid metric threshold or series.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostMetricThresholdView>(cancellationToken: token) ?? throw new InvalidOperationException("Empty threshold response.");
    }
}
