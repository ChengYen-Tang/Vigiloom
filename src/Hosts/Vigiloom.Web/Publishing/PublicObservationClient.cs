using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Web.Publishing;

internal sealed class PublicObservationClient(HttpClient client)
{
    public async Task<Dictionary<Guid, PublicObservationVersions>> VersionsAsync(Guid[] ids, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/public-observation/versions", ids, token);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<PublicObservationVersions[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing observation versions.");
        return rows.ToDictionary(row => row.Id);
    }
    public async Task<IReadOnlyDictionary<Guid, HealthStatusReader.OverviewStatus>> StatusesAsync(Guid[] ids, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/public-observation/statuses", ids, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<Guid, HealthStatusReader.OverviewStatus>>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing observation status.");
    }
    public async Task<DateTimeOffset> TimeAsync(CancellationToken token) => await client.GetFromJsonAsync<DateTimeOffset>("internal/v1/public-observation/time", token);
    public async Task<ServiceHealthHistory?> HistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString($"internal/v1/health/{id:D}/history", new Dictionary<string, string?>
        { ["from"] = from.ToString("O", CultureInfo.InvariantCulture), ["to"] = to.ToString("O", CultureInfo.InvariantCulture) });
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity) throw new HealthHistoryTooLargeException();
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) throw new HealthHistoryBusyException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceHealthHistory>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing health history.");
    }
}
