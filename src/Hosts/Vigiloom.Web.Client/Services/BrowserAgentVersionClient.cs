using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserAgentVersionClient(HttpClient client) : IAgentVersionClient
{
    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    {
        using var response = await client.GetAsync($"api/v1/agents/{agentId:D}/version-assignment", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentVersionAssignmentView>(cancellationToken: token);
    }

    public async Task<AgentPackagePage> PackagesAsync(int page, CancellationToken token)
    {
        using var response = await client.GetAsync($"api/v1/agent-packages?page={page}", token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentPackagePage>(cancellationToken: token) ?? throw new HttpRequestException("Empty package response.");
    }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, AgentVersionAssignmentUpdate update, CancellationToken token)
    {
        var csrf = await client.GetFromJsonAsync<AntiforgeryToken>("api/v1/session/antiforgery", token) ?? throw new HttpRequestException("Missing antiforgery token.");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/agents/{agentId:D}/version-assignment") { Content = JsonContent.Create(update) };
        request.Headers.Add("RequestVerificationToken", csrf.RequestToken);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        if (response.StatusCode == HttpStatusCode.NotFound) return VersionAssignmentResult.NotFound;
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest) return VersionAssignmentResult.Conflict;
        response.EnsureSuccessStatusCode();
        return VersionAssignmentResult.Saved;
    }

    private sealed record AntiforgeryToken(string RequestToken);
}
