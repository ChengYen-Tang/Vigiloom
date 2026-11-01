using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Workspace;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserWorkspaceIdentityClient(HttpClient client) : IWorkspaceIdentityClient
{
    public async Task<WorkspaceIdentity?> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("api/v1/workspace/identity", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkspaceIdentity>(cancellationToken: cancellationToken);
    }
}
