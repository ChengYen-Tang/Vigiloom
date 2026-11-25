using System.Net;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentPackages;

internal sealed class RemoteAgentUpdateAccess(HttpClient client, IMachineAgentIdentity identity) : IAgentUpdateAccess
{
    public async Task<byte[]?> ReadManifestAsync(string? credential, string name, CancellationToken token)
    {
        var agent = await identity.AuthenticateAsync(credential, token);
        if (agent is null) return null;
        using var response = await client.GetAsync($"internal/v1/agents/{agent.AgentId:D}/update/targets/{Uri.EscapeDataString(name)}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(token);
    }

    public async Task<Stream?> OpenArchiveAsync(string? credential, string name, CancellationToken token)
    {
        var agent = await identity.AuthenticateAsync(credential, token);
        if (agent is null) return null;
        try { return await client.GetStreamAsync($"internal/v1/agents/{agent.AgentId:D}/update/packages/{Uri.EscapeDataString(name)}", token); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
}
