using System.Net;

namespace Vigiloom.HostMonitoring;

internal static class ResourceDevices
{
    public static async Task<bool> ExistsAsync(IHttpClientFactory clients, Guid id, CancellationToken token)
    {
        using var response = await clients.CreateClient("resources").GetAsync($"internal/v1/devices/{id:D}/agent", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
