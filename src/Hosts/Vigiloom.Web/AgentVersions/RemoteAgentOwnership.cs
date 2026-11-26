using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class RemoteAgentOwnership(HttpClient client) : IDeviceInventoryAgent, IAgentOwnerResolver
{
    async Task<(bool Exists, Guid? AgentId)> IDeviceInventoryAgent.ResolveAsync(Guid deviceId, CancellationToken token)
    {
        var ownership = await ReadAsync(deviceId, token);
        return ownership is null ? (false, null) : (true, ownership.Registration?.Id);
    }

    async Task<InventoryOwner?> IAgentOwnerResolver.ResolveAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId.Version != 7) throw new ArgumentException("Invalid device identity.", nameof(deviceId));
        var ownership = await ReadAsync(deviceId, token);
        return ownership is { Device.Eligible: true, Registration: { RevokedAt: null } agent }
            ? new(agent.Id, deviceId, ownership.Device.Generation) : null;
    }

    private async Task<DeviceAgentOwnership?> ReadAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agent-ownership", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceAgentOwnership>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned empty Agent ownership.");
    }
}
