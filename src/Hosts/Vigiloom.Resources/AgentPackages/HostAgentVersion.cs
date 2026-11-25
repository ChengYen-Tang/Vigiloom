using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentPackages;

namespace Vigiloom.Resources.AgentPackages;

internal sealed class HostAgentVersion(HttpClient client, IConfiguration configuration, ILogger<HostAgentVersion> logger) : IHostAgentVersion
{
    public async Task<AgentVersionObservation?> GetAsync(Guid agentId, CancellationToken token)
    {
        var baseUrl = configuration["Services:Host:Url"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        try
        {
            using var response = await client.GetAsync(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), $"internal/v1/agents/{agentId:D}/update-status"), token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AgentVersionObservation>(cancellationToken: token);
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning(error, "Could not read Agent version status from host monitoring.");
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Timed out reading Agent version status from host monitoring.");
            return null;
        }
    }
}
