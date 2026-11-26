using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.SpeedTests;

namespace Vigiloom.Web.SpeedTests;

internal sealed class RemoteSpeedHistory(HttpClient client)
{
    public async Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/speed/history", query, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SpeedHistoryPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned empty speed history.");
    }
    public async Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/speed/history/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SpeedRunDetails>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty speed result.");
    }
}
