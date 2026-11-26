using System.Net.Http.Json;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Web.Network;

internal sealed class RemotePingStatusReader(HttpClient client) : IPingStatusReader
{
    public async Task<DevicePingStatus> GetStatusAsync(Guid id, IReadOnlyList<string> targets, string deviceVersion, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/ping/status", new PingStatusRequest(id, targets, deviceVersion), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DevicePingStatus>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty Ping status.");
    }

    public async Task<IReadOnlyDictionary<Guid, DevicePingStatus>> GetStatusesAsync(IReadOnlyList<PingDeviceSnapshot> devices, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/ping/statuses", devices, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<Guid, DevicePingStatus>>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned empty Ping statuses.");
    }
}
