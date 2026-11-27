using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class DetectionAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    public Task<SigmaDetectionEventPage?> GetEventsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => Remote
        ? ReadAsync<SigmaDetectionEventPage>(deviceId, "sigma", from, until, cursor, null, token) : services.GetRequiredService<SigmaDetectionHistory>().GetEventsAsync(deviceId, from, until, cursor, token);
    public Task<SigmaDetectionGapPage?> GetGapsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => Remote
        ? ReadAsync<SigmaDetectionGapPage>(deviceId, "sigma-gaps", from, until, cursor, null, token) : services.GetRequiredService<SigmaDetectionHistory>().GetGapsAsync(deviceId, from, until, cursor, token);
    public Task<IocDetectionPage?> GetIocAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, int page, CancellationToken token) => Remote
        ? ReadAsync<IocDetectionPage>(deviceId, "ioc", from, until, null, page, token) : services.GetRequiredService<IocDetectionHistory>().GetAsync(deviceId, from, until, page, token);

    private async Task<T?> ReadAsync<T>(Guid deviceId, string kind, DateTimeOffset from, DateTimeOffset until, string? cursor, int? page, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString($"internal/v1/devices/{deviceId:D}/detections/{kind}", new Dictionary<string, string?>
        {
            ["from"] = from.ToString("O"), ["until"] = until.ToString("O"), ["cursor"] = cursor, ["page"] = page?.ToString()
        });
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid detection query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty detection response.");
    }
}
