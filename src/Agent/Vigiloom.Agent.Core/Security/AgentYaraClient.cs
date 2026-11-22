using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Security;

namespace Vigiloom.Agent.Core.Security;

/// <summary>Reads scan jobs using the Agent's registered identity.</summary>
public sealed class AgentYaraClient(HttpClient http)
{
    public async Task<YaraScanJob?> NextAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var (uri, credential) = await CredentialsAsync(vault, server, token);
        if (credential is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "api/v1/agent/yara-scans/next"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(512 * 1024, token);
        return await response.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token);
    }

    private static async Task<(Uri Server, string? Credential)> CredentialsAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var uri = IdentityClient.NormalizeServer(server);
        var (identity, _) = await IdentityClient.ReadRegisteredAsync(vault, uri, token);
        return (uri, identity is null ? null : $"vla_{identity.AgentId!.Value:D}.{identity.MachineSecret}");
    }
}
