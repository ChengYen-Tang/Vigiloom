using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Web.Services;

internal sealed class HealthAdmin(HttpClient client, IConfiguration configuration, HealthTargetAdministration localTargets,
    HealthNetworkPolicyAdministration localNetworks, HealthObservationReader localObservations)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);
    public Task<HealthNetworkPolicySnapshot> GetNetworksAsync(CancellationToken token) => Local ? localNetworks.GetAsync(token) : RequiredAsync<HealthNetworkPolicySnapshot>("internal/v1/health-admin/networks", token);
    public async Task<IReadOnlyList<HealthNetworkRevisionSnapshot>> ListRevisionsAsync(int page, CancellationToken token) => Local ? await localNetworks.ListRevisionsAsync(page, token) : await RequiredAsync<HealthNetworkRevisionSnapshot[]>($"internal/v1/health-admin/networks/history?page={page}", token);
    public Task<HealthNetworkPolicySnapshot?> UpdateNetworksAsync(HealthNetworkPolicyInput input, Guid actor, CancellationToken token) => Local ? localNetworks.UpdateAsync(input, actor, token) : SendAsync<HealthNetworkPolicySnapshot>(HttpMethod.Put, "internal/v1/health-admin/networks", new UpdateHealthNetworkCommand(input, actor), token);
    public Task<HealthTargetSnapshot?> GetTargetAsync(Guid id, CancellationToken token) => Local ? localTargets.GetAsync(id, token) : GetAsync<HealthTargetSnapshot>($"internal/v1/health-admin/targets/{id:D}", token);
    public async Task<IReadOnlyList<HealthTargetSnapshot>> ListTargetsAsync(Guid id, int page, CancellationToken token) => Local ? await localTargets.ListAsync(id, page, token) : await RequiredAsync<HealthTargetSnapshot[]>($"internal/v1/health-admin/targets?serviceId={id:D}&page={page}", token);
    public async Task<IReadOnlyList<HealthCheckObservation>> LatestAsync(Guid id, int page, CancellationToken token) => Local ? await localObservations.LatestAsync(id, page, token) : await RequiredAsync<HealthCheckObservation[]>($"internal/v1/health-admin/observations?serviceId={id:D}&page={page}", token);
    public async Task<HealthTargetResult> CreateAsync(CreateHealthTargetInput input, Guid actor, CancellationToken token) => Local ? await localTargets.CreateAsync(input, actor, token) : await SendAsync<HealthTargetResult>(HttpMethod.Post, "internal/v1/health-admin/targets", new CreateHealthTargetCommand(input, actor), token) ?? throw new InvalidOperationException("Missing health result.");
    public async Task<HealthTargetResult> UpdateAsync(Guid id, HealthTargetInput input, Guid actor, CancellationToken token) => Local ? await localTargets.UpdateAsync(id, input, actor, token) : await SendAsync<HealthTargetResult>(HttpMethod.Put, $"internal/v1/health-admin/targets/{id:D}", new UpdateHealthTargetCommand(input, actor), token) ?? throw new InvalidOperationException("Missing health result.");
    private async Task<T> RequiredAsync<T>(string path, CancellationToken token) => await GetAsync<T>(path, token) ?? throw new InvalidOperationException("Missing health data.");
    private async Task<T?> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return default;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid health configuration.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }
}
