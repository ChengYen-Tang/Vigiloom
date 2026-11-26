using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application;
using ServiceDetailsDto = Vigiloom.Contracts.Services.ServiceDetails;

namespace Vigiloom.Web.ServiceDetails;

/// <summary>Uses the observation service when deployed, otherwise the local module.</summary>
internal sealed class ServiceQueries(HttpClient client, IConfiguration configuration,
    HealthStatusReader status, HealthHistoryReader history, ServiceDetailsReader details)
{
    public Task<ServiceDetailsDto?> DetailAsync(Guid id, CancellationToken token) =>
        string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"])
            ? details.ReadAsync(id, token) : GetAsync<ServiceDetailsDto>($"internal/v1/services/{id:D}", token);

    public Task<ServiceInstancePage?> InstancesAsync(Guid id, int page, CancellationToken token) =>
        string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"])
            ? details.InstancesAsync(id, page, token) : GetAsync<ServiceInstancePage>($"internal/v1/services/{id:D}/instances?page={page}", token);

    public Task<ServiceHealthStatus?> StatusAsync(Guid id, CancellationToken token) =>
        string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"])
            ? status.ReadAsync(id, token) : GetAsync<ServiceHealthStatus>($"internal/v1/health/{id:D}/status", token);

    public Task<ServiceHealthHistory?> HistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"])) return history.ReadAsync(id, from, to, token);
        var path = QueryHelpers.AddQueryString($"internal/v1/health/{id:D}/history", new Dictionary<string, string?>
        {
            ["from"] = from.ToString("O", CultureInfo.InvariantCulture), ["to"] = to.ToString("O", CultureInfo.InvariantCulture)
        });
        return GetAsync<ServiceHealthHistory>(path, token);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Observation service returned an empty health response.");
    }
}
