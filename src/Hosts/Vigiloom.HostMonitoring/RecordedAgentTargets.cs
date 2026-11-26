using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal sealed class RecordedAgentTargets(HttpClient client) : IRecordedAgentTargetReader
{
    public async Task<RecordedAgentUpdateTarget?> GetAsync(Guid agentId, Guid updateId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/agents/{agentId:D}/update-targets/{updateId:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecordedAgentUpdateTarget>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned an empty Agent update target.");
    }
}
