using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal sealed class OperationsLogIdentities(HttpClient client) : IHostLogIdentities
{
    public async Task<IReadOnlyCollection<Guid>> ExistingAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/host-logs/identities", new HostLogReadRequest(agentId, messageIds), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations returned an empty Log identity response.");
    }
}
