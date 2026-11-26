using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Services;

internal sealed class SourceManagement(HttpClient client, IConfiguration configuration, SourceAdministration local)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);

    public Task<IReadOnlyList<ServiceSourceSummary>> ListAsync(int page, CancellationToken token) =>
        Local ? local.ListAsync(page, token) : ListRemoteAsync(page, token);
    private async Task<IReadOnlyList<ServiceSourceSummary>> ListRemoteAsync(int page, CancellationToken token) =>
        await client.GetFromJsonAsync<ServiceSourceSummary[]>($"internal/v1/observation-sources?page={page}", token)
        ?? throw new InvalidOperationException("Observation service returned an empty source list.");

    public Task<SourceCredentialIssue> IssueAsync(string name, Guid actor, CancellationToken token) =>
        Local ? local.IssueAsync(name, actor, token) : PostAsync<SourceCredentialIssue>("internal/v1/observation-sources", new IssueServiceSourceCommand(name, actor), token);

    public async Task<bool> RevokeAsync(Guid id, Guid actor, CancellationToken token)
    {
        if (Local) return await local.RevokeAsync(id, actor, token);
        using var response = await client.PostAsJsonAsync($"internal/v1/observation-sources/{id:D}/revoke", new RevokeServiceSourceCommand(actor), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
    public Task<SourceRotationResult> RotateAsync(Guid id, byte[] version, Guid actor, CancellationToken token) =>
        Local ? local.RotateAsync(id, version, actor, token) : PostAsync<SourceRotationResult>($"internal/v1/observation-sources/{id:D}/rotate", new RotateServiceSourceCommand(version, actor), token);

    private async Task<T> PostAsync<T>(string path, object command, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, command, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Observation service returned an empty response.");
    }
}
