using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using Vigiloom.Contracts.Services;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserServiceDetailsClient(HttpClient client) : IServiceDetailsClient
{
    public Task<ServiceDetails?> GetServiceAsync(Guid id, CancellationToken token) => ReadAsync<ServiceDetails>($"api/v1/service-details/{id:D}/", token);
    public Task<ServiceInstancePage?> GetInstancesAsync(Guid id, int page, CancellationToken token) => ReadAsync<ServiceInstancePage>($"api/v1/service-details/{id:D}/instances?page={page}", token);
    public Task<DeploymentLinkPage?> GetDeploymentsAsync(Guid id, int page, CancellationToken token) => ReadAsync<DeploymentLinkPage>($"api/v1/service-details/{id:D}/deployments?page={page}", token);
    public Task<ServiceHealthStatus?> GetHealthAsync(Guid id, CancellationToken token) => ReadAsync<ServiceHealthStatus>($"api/v1/service-health/{id:D}/status", token);
    public async Task<HealthObservationPage> GetHealthEvidenceAsync(Guid id, int page, CancellationToken token) =>
        await ReadAsync<HealthObservationPage>($"api/v1/service-details/{id:D}/health-observations?page={page}", token) ?? throw new HttpRequestException("Missing health observations.");
    public Task<ServiceHealthHistory?> GetHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) =>
        ReadAsync<ServiceHealthHistory>($"api/v1/service-health/{id:D}/history?from={Escape(from)}&to={Escape(to)}", token);
    public async Task<ServiceHttpMetrics> GetHttpMetricsAsync(Guid id, CancellationToken token) =>
        await ReadAsync<ServiceHttpMetrics>($"api/v1/service-details/{id:D}/http-metrics", token) ?? throw new HttpRequestException("Missing HTTP metrics.");
    public async Task<ServiceEvidencePage<ServiceLogEvidence>> GetLogsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, string? traceId, CancellationToken token) =>
        await ReadAsync<ServiceEvidencePage<ServiceLogEvidence>>($"api/v1/service-details/{id:D}/logs?from={Escape(from)}&to={Escape(to)}&page={page}{(traceId is null ? "" : $"&traceId={Uri.EscapeDataString(traceId)}")}", token) ?? throw new HttpRequestException("Missing log evidence.");
    public async Task<ServiceEvidencePage<ServiceTraceEvidence>> GetTracesAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token) =>
        await ReadAsync<ServiceEvidencePage<ServiceTraceEvidence>>($"api/v1/service-details/{id:D}/traces?from={Escape(from)}&to={Escape(to)}&page={page}", token) ?? throw new HttpRequestException("Missing trace evidence.");

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new HttpRequestException("Empty service response.");
    }

    private static string Escape(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));
}
