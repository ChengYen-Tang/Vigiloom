using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Network;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserPingHistoryClient(HttpClient client) : IPingHistoryClient
{
    public async Task<PingHistoryPage?> QueryAsync(PingHistoryQuery q, CancellationToken token)
    {
        var url = $"api/v1/devices/{q.DeviceId:D}/ping/history?from={Uri.EscapeDataString(q.From.ToString("O"))}&to={Uri.EscapeDataString(q.To.ToString("O"))}&location={Uri.EscapeDataString(q.Location)}&page={q.Page}&descending={q.Descending}";
        if (q.Target is not null) url += $"&target={Uri.EscapeDataString(q.Target)}";
        using var response = await client.GetAsync(url, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PingHistoryPage>(cancellationToken: token) ?? throw new HttpRequestException("Empty Ping history response.");
    }
}
