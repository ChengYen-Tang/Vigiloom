using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserAgentRolloutClient(HttpClient client) : IAgentRolloutClient
{
    public async Task<IReadOnlyList<AgentRolloutSummary>> ListAsync(Guid groupId, CancellationToken token) =>
        await client.GetFromJsonAsync<AgentRolloutSummary[]>($"api/v1/groups/{groupId:D}/agent-rollouts", token) ?? [];

    public async Task<AgentRolloutView?> GetAsync(Guid id, CancellationToken token) =>
        await ReadAsync<AgentRolloutView>($"api/v1/agent-rollouts/{id:D}", token);

    public async Task<AgentRolloutPreview?> PreviewAsync(Guid groupId, Guid packageId, CancellationToken token) =>
        await ReadAsync<AgentRolloutPreview>($"api/v1/groups/{groupId:D}/agent-rollouts/preview?packageId={packageId:D}", token);

    public async Task<AgentRolloutCreation> CreateAsync(AgentRolloutInput input, CancellationToken token)
    {
        using var response = await PostAsync("api/v1/agent-rollouts", input, token);
        if (!response.IsSuccessStatusCode) return new(Result(response.StatusCode));
        var created = await response.Content.ReadFromJsonAsync<AgentRolloutCreated>(cancellationToken: token)
            ?? throw new HttpRequestException("Empty rollout response.");
        return new(AgentRolloutResult.Saved, created.Id, created.Included, created.Excluded);
    }

    public async Task<AgentRolloutResult> ChangeAsync(Guid id, AgentRolloutChange input, CancellationToken token)
    {
        using var response = await PostAsync($"api/v1/agent-rollouts/{id:D}/state", input, token);
        return response.IsSuccessStatusCode ? AgentRolloutResult.Saved : Result(response.StatusCode);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T input, CancellationToken token)
    {
        var csrf = await client.GetFromJsonAsync<AntiforgeryToken>("api/v1/session/antiforgery", token)
            ?? throw new HttpRequestException("Missing antiforgery token.");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
        request.Headers.Add("RequestVerificationToken", csrf.RequestToken);
        return await client.SendAsync(request, token);
    }

    private static AgentRolloutResult Result(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => AgentRolloutResult.Invalid,
        HttpStatusCode.NotFound => AgentRolloutResult.NotFound,
        HttpStatusCode.Conflict => AgentRolloutResult.Conflict,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => throw new UnauthorizedAccessException(),
        _ => throw new HttpRequestException($"Rollout request failed: {(int)status}.")
    };
    private sealed record AntiforgeryToken(string RequestToken);
}
