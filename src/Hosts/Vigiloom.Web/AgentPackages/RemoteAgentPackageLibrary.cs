using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentPackages;

internal sealed class RemoteAgentPackageLibrary(HttpClient client) : IAgentPackageLibrary
{
    public async Task<bool> CacheAsync(string version, string runtimeIdentifier, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/agent-packages/download", new { version, runtimeIdentifier }, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("The package conflicts with an existing assignment.");
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<PackageRemovalResult> RemoveAsync(Guid packageId, CancellationToken token)
    {
        using var response = await client.DeleteAsync($"internal/v1/agent-packages/{packageId:D}/cache", token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AgentPackageRemovalResult>(cancellationToken: token);
        return result switch
        {
            AgentPackageRemovalResult.Removed => PackageRemovalResult.Removed,
            AgentPackageRemovalResult.InUse => PackageRemovalResult.InUse,
            _ => PackageRemovalResult.NotFound
        };
    }
}
