using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Services;

public interface IApplicationManagement
{
    Task<IReadOnlyList<ManagedServiceSummary>> ListServicesAsync(int page, CancellationToken token);
    Task<ManagedServiceSummary?> GetServiceAsync(Guid id, CancellationToken token);
    Task<ServiceCollectionStatus> SetStoppedAsync(Guid id, string version, bool stopped, Guid actor, CancellationToken token);
    Task<ServiceCollectionStatus> SetArchivedAsync(Guid id, string version, bool archived, Guid actor, CancellationToken token);
}

internal sealed class LocalApplicationManagement(ServiceInstanceAdministration instances, ServiceCollectionAdministration lifecycle) : IApplicationManagement
{
    public Task<IReadOnlyList<ManagedServiceSummary>> ListServicesAsync(int page, CancellationToken token) => instances.ListServicesAsync(page, token);
    public Task<ManagedServiceSummary?> GetServiceAsync(Guid id, CancellationToken token) => instances.GetServiceAsync(id, token);
    public Task<ServiceCollectionStatus> SetStoppedAsync(Guid id, string version, bool stopped, Guid actor, CancellationToken token) => lifecycle.SetStoppedAsync(id, version, stopped, actor, token);
    public Task<ServiceCollectionStatus> SetArchivedAsync(Guid id, string version, bool archived, Guid actor, CancellationToken token) => lifecycle.SetArchivedAsync(id, version, archived, actor, token);
}

public sealed class RemoteApplicationManagement(HttpClient client, ApplicationStateProjection projection) : IApplicationManagement
{
    private const string Root = "internal/v1/application-services";
    public async Task<IReadOnlyList<ManagedServiceSummary>> ListServicesAsync(int page, CancellationToken token) =>
        await client.GetFromJsonAsync<ManagedServiceSummary[]>($"{Root}?page={page}", token) ?? throw new InvalidOperationException("Resource service returned an empty service list.");
    public async Task<ManagedServiceSummary?> GetServiceAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"{Root}/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ManagedServiceSummary>(cancellationToken: token);
    }
    public Task<ServiceCollectionStatus> SetStoppedAsync(Guid id, string version, bool stopped, Guid actor, CancellationToken token) =>
        ChangeAsync(id, version, stopped ? "stop" : "resume", actor, token);
    public Task<ServiceCollectionStatus> SetArchivedAsync(Guid id, string version, bool archived, Guid actor, CancellationToken token) =>
        ChangeAsync(id, version, archived ? "archive" : "restore", actor, token);
    private async Task<ServiceCollectionStatus> ChangeAsync(Guid id, string version, string action, Guid actor, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"{Root}/{id:D}/state", new ApplicationStateChange(version, action, actor), token);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ApplicationStateChangeStatus>(cancellationToken: token)) switch
        {
            ApplicationStateChangeStatus.Updated => ServiceCollectionStatus.Updated,
            ApplicationStateChangeStatus.NotFound => ServiceCollectionStatus.NotFound,
            ApplicationStateChangeStatus.SourceUnavailable => ServiceCollectionStatus.SourceUnavailable,
            _ => ServiceCollectionStatus.Conflict
        };
        if (result is ServiceCollectionStatus.Updated or ServiceCollectionStatus.Conflict)
        {
            await projection.ApplyAsync(id, GetServiceAsync, token);
        }
        return result;
    }
}
