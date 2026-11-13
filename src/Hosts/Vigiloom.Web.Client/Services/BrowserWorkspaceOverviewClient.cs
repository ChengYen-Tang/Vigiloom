using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Workspace;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserWorkspaceOverviewClient(HttpClient client) : IWorkspaceOverviewClient
{
    public async Task<WorkspaceOverview?> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("api/v1/workspace/overview", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkspaceOverview>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Empty workspace overview response.");
    }
}
