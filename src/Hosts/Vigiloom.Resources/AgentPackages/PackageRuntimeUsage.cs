using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentPackages;

namespace Vigiloom.Resources.AgentPackages;

internal sealed class PackageRuntimeUsage(HttpClient client, IConfiguration configuration) : IPackageRuntimeUsage
{
    public async Task<bool> IsInstalledAsync(string version, string runtimeIdentifier, CancellationToken token)
    {
        var baseUrl = configuration["Services:Host:Url"];
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Services:Host:Url is required to check Agent package usage.");
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "internal/v1/agent-package-usage"),
            new AgentPackageUsageRequest(version, runtimeIdentifier), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentPackageUsageResponse>(cancellationToken: token))?.InUse
            ?? throw new InvalidOperationException("Host service returned an empty package usage response.");
    }
}
