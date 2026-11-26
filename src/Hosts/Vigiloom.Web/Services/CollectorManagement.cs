using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Services;

internal sealed class CollectorManagement(HttpClient client, IConfiguration configuration, CollectorAdministration local,
    CollectorObservations observations, CollectorCounters counters)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);
    public Task<CollectorSummary?> GetAsync(Guid id, CancellationToken token) => Local ? local.GetAsync(id, token) : GetAsync<CollectorSummary>($"internal/v1/collectors/{id:D}", token);
    public async Task<IReadOnlyList<CollectorSummary>> ListAsync(int page, CancellationToken token) => Local ? await local.ListAsync(page, token) :
        await GetAsync<CollectorSummary[]>($"internal/v1/collectors?page={page}", token) ?? throw new InvalidOperationException("Missing Collector list.");
    public async Task<IReadOnlyList<CollectorRouteSummary>> RoutesAsync(Guid id, CancellationToken token) => Local ? await local.RoutesAsync(id, token) :
        await GetAsync<CollectorRouteSummary[]>($"internal/v1/collectors/{id:D}/routes", token) ?? throw new InvalidOperationException("Missing Collector routes.");
    public Task<CollectorObservationPage?> ObservationsAsync(Guid id, int page, CancellationToken token) => Local ? observations.QueryAsync(id, page, token) :
        GetAsync<CollectorObservationPage>($"internal/v1/collectors/observations/{id:D}?page={page}", token);
    public Task<CollectorCounterPage?> CountersAsync(Guid id, Guid source, Guid instance, CancellationToken token) => Local ? counters.QueryAsync(id, source, instance, token) :
        GetAsync<CollectorCounterPage>($"internal/v1/collectors/{id:D}/sources/{source:D}/instances/{instance:D}/counters", token);
    public Task<CollectorChangeResult> RegisterAsync(string name, Guid service, bool confirmed, Guid actor, CancellationToken token) =>
        Local ? local.RegisterAsync(name, service, confirmed, actor, token) : PostAsync<CollectorChangeResult>("internal/v1/collectors", new RegisterCollectorCommand(name, service, confirmed, actor), token);
    public Task<CollectorChangeStatus> SetRouteAsync(Guid id, Guid source, string version, string? exporter, bool add, Guid actor, CancellationToken token) =>
        Local ? local.SetRouteAsync(id, source, version, exporter, add, actor, token) : PostAsync<CollectorChangeStatus>($"internal/v1/collectors/{id:D}/routes", new SetCollectorRouteCommand(source, version, exporter, add, actor), token);
    private async Task<T?> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }
    private async Task<T> PostAsync<T>(string path, object body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, body, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid Collector request.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing Collector response.");
    }
}
