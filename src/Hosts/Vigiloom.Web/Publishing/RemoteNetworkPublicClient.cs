using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Publishing;

internal sealed class RemoteNetworkPublicClient(HttpClient client)
{
    public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken token) => GetAsync<PublicDevice>($"api/v1/public/devices/{id:D}/ping", token);
    public async Task<PublicDevice[]> DevicesAsync(Guid[] ids, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/public-ping", ids, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PublicDevice[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty response.");
    }
    public Task<PublicDeviceHistory?> HistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) =>
        GetAsync<PublicDeviceHistory>(QueryHelpers.AddQueryString($"api/v1/public/devices/{id:D}/ping/history", new Dictionary<string, string?>
        { ["from"] = from.ToString("O", CultureInfo.InvariantCulture), ["to"] = to.ToString("O", CultureInfo.InvariantCulture) }), token);

    private async Task<T?> GetAsync<T>(string path, CancellationToken token) where T : class
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty response.");
    }
}
