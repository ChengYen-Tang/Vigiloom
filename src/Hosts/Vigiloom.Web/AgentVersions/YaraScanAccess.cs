using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class YaraScanAccess(YaraScans legacy, IAgentOwnerResolver owners, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);

    public async Task<YaraScanJob?> CreateAsync(Guid deviceId, YaraScanRequest request, Guid actor, CancellationToken token)
    {
        if (!Remote) return await legacy.CreateAsync(deviceId, request, actor, token);
        var owner = await owners.ResolveAsync(deviceId, token);
        if (owner is null) return null;
        var command = new YaraCreateCommand(new RegisteredAgentIdentity(owner.AgentId, owner.DeviceId, owner.DeviceGeneration), request, actor);
        using var response = await clients.CreateClient("security").PostAsJsonAsync("internal/v1/yara-scans/create", command, token);
        EnsureValid(response);
        return await response.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security service returned an empty scan.");
    }

    public async Task<IReadOnlyList<YaraScanJob>> ListAsync(Guid deviceId, CancellationToken token)
    {
        if (!Remote) return await legacy.ListAsync(deviceId, token);
        using var response = await clients.CreateClient("security").GetAsync($"internal/v1/yara-scans/devices/{deviceId:D}", token);
        EnsureValid(response);
        return await response.Content.ReadFromJsonAsync<YaraScanJob[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security service returned an empty list.");
    }

    public async Task<YaraScanJob?> GetAsync(Guid deviceId, Guid scanId, CancellationToken token)
    {
        if (!Remote) return await legacy.GetAsync(deviceId, scanId, token);
        using var response = await clients.CreateClient("security").GetAsync($"internal/v1/yara-scans/devices/{deviceId:D}/{scanId:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureValid(response);
        return await response.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token);
    }

    public async Task<bool> CancelAsync(Guid deviceId, Guid scanId, CancellationToken token)
    {
        if (!Remote) return await legacy.CancelAsync(deviceId, scanId, token);
        using var response = await clients.CreateClient("security").PostAsync($"internal/v1/yara-scans/devices/{deviceId:D}/{scanId:D}/cancel", null, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        EnsureValid(response);
        return true;
    }

    private static void EnsureValid(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid scan request.");
        response.EnsureSuccessStatusCode();
    }
}
