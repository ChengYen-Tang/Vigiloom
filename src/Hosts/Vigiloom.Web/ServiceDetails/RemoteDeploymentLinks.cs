using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;

namespace Vigiloom.Web.ServiceDetails;

internal sealed class RemoteDeploymentLinks(HttpClient client) : IDeploymentLinks
{
    public async Task<DeploymentLinkPage?> ListAsync(Guid serviceId, int page, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/application-services/{serviceId:D}/deployments?page={page}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeploymentLinkPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing deployment relationships.");
    }

    public async Task<DeploymentLinkResult> CreateAsync(Guid serviceId, CreateDeploymentLink input, Guid actor, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"internal/v1/application-services/{serviceId:D}/deployments", new CreateDeploymentLinkCommand(input, actor), token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DeploymentLinkMutationResult>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing deployment result.");
        return new((DeploymentLinkStatus)result.Status, result.Id);
    }

    public async Task<DeploymentLinkStatus> EndAsync(Guid serviceId, Guid id, EndDeploymentLink input, Guid actor, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"internal/v1/application-services/{serviceId:D}/deployments/{id:D}/end", new EndDeploymentLinkCommand(input, actor), token);
        response.EnsureSuccessStatusCode();
        return (DeploymentLinkStatus)await response.Content.ReadFromJsonAsync<DeploymentLinkMutationStatus>(cancellationToken: token);
    }
}
