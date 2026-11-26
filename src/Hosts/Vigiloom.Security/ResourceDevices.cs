using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Security;

internal sealed class ResourceDevices(HttpClient client) : IDeviceAgentSource
{
    public async Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceAgentState>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned an empty device.");
    }

    public Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
        throw new NotSupportedException("A remote device cannot participate in a local database transaction.");
}
