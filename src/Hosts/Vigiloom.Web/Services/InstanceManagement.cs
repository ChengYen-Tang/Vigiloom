using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Services;

internal sealed class InstanceManagement(HttpClient client, IConfiguration configuration, ServiceInstanceAdministration local,
    InstancePolicyAdministration localPolicy)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);
    public Task<InstancePolicySnapshot> GetPolicyAsync(CancellationToken token) => Local ? localPolicy.GetAsync(token) : RequiredAsync<InstancePolicySnapshot>("internal/v1/instance-admin/policy", token);
    public Task<InstancePolicySnapshot?> UpdatePolicyAsync(InstancePolicyInput input, Guid actor, CancellationToken token) => Local ? localPolicy.UpdateAsync(input, actor, token) : SendAsync<InstancePolicySnapshot>(HttpMethod.Put, "internal/v1/instance-admin/policy", new UpdateInstancePolicyCommand(input, actor), token);
    public async Task<IReadOnlyList<ServiceInstanceSummary>> ListAsync(Guid id, int page, CancellationToken token) => Local ? await local.ListAsync(id, page, token) : await RequiredAsync<ServiceInstanceSummary[]>($"internal/v1/instance-admin/services/{id:D}?page={page}", token);
    public async Task<IReadOnlyList<ServiceInstanceSummary>> GetInstancesAsync(Guid id, IReadOnlyCollection<Guid> ids, CancellationToken token) => Local ? await local.GetInstancesAsync(id, ids, token) : await SendAsync<ServiceInstanceSummary[]>(HttpMethod.Post, $"internal/v1/instance-admin/services/{id:D}/lookup", ids, token) ?? throw new InvalidOperationException("Missing instances.");
    public async Task<InstanceChangeStatus> SetRetiredAsync(Guid id, string version, bool retired, Guid actor, CancellationToken token) => Local ? await local.SetRetiredAsync(id, version, retired, actor, token) : await SendAsync<InstanceChangeStatus>(HttpMethod.Post, $"internal/v1/instance-admin/{id:D}", new ChangeInstanceCommand(version, retired, actor), token);
    private async Task<T> RequiredAsync<T>(string path, CancellationToken token) => await client.GetFromJsonAsync<T>(path, token) ?? throw new InvalidOperationException("Missing instance data.");
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return default;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid instance configuration.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }
}
