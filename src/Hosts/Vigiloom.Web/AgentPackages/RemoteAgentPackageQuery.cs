using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentPackages;

internal sealed class RemoteAgentPackageQuery(HttpClient client) : IAgentPackageQuery
{
    public async Task<AgentPackagePage> GetAsync(int page, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgentPackagePage>($"internal/v1/agent-packages?page={page}", cancellationToken)
        ?? throw new InvalidOperationException("Resource service returned an empty package list.");

    public async Task<IReadOnlyList<AgentPackageView>> GetVersionsAsync(string[] versions, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/agent-packages/versions", versions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentPackageView[]>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Resource service returned an empty package list.");
    }
}
