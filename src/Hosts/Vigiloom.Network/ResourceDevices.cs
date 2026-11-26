using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Devices;

namespace Vigiloom.Network;

internal sealed class ResourceDevices(HttpClient client)
{
    public async Task<DeviceDetails?> GetAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/device-management/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token)
            ?? throw new InvalidOperationException("The resource service returned an empty device.");
    }
}
