using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Security;

internal sealed class ResourceDevices(HttpClient client) : IDeviceAgentSource, IAgentOwnerResolver
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

    public async Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId.Version != 7) throw new ArgumentException("Invalid device identity.", nameof(deviceId));
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-ownership", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var ownership = await response.Content.ReadFromJsonAsync<DeviceAgentOwnership>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned empty Agent ownership.");
        return ownership is { Device.Eligible: true, Registration: { RevokedAt: null } agent }
            ? new(agent.Id, deviceId, ownership.Device.Generation) : null;
    }
}
