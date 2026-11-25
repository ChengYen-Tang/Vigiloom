using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class RemoteAgentAssignments(HttpClient client) : IAgentAssignments
{
    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/agents/{agentId:D}/assignment", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentVersionAssignmentView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty assignment.");
    }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, Guid? packageId, string? revision, Guid actorId, CancellationToken token)
    {
        if (revision is null) return VersionAssignmentResult.Conflict;
        using var response = await client.PutAsJsonAsync($"internal/v1/agents/{agentId:D}/assignment",
            new AgentVersionAssignmentCommand(packageId, revision, actorId), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VersionAssignmentResult>(cancellationToken: token);
    }
}
