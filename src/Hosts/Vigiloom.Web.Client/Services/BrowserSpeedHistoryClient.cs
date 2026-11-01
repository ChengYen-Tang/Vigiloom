using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.SpeedTests;
namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserSpeedHistoryClient(HttpClient client) : ISpeedHistoryClient
{
    public Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token) => ReadAsync<SpeedHistoryPage>(
        $"api/v1/speed-tests?from={Uri.EscapeDataString(query.From.ToString("O"))}&to={Uri.EscapeDataString(query.To.ToString("O"))}&search={Uri.EscapeDataString(query.Search ?? "")}&sort={Uri.EscapeDataString(query.Sort)}&descending={query.Descending}&page={query.Page}", token);
    public async Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"api/v1/speed-tests/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<SpeedRunDetails>(response, token);
    }
    private async Task<T> ReadAsync<T>(string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, token);
        return await ReadAsync<T>(response, token);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new HttpRequestException("Empty speed-test response.");
    }
}
