using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteHostLogSettings(HttpClient client) : IHostLogConfiguration
{
    public async Task<IReadOnlyList<HostLogSourceView>?> ListAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync(Path(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostLogSourceView[]>(cancellationToken: token) ?? throw new InvalidOperationException("Empty host log sources response.");
    }

    public Task<HostLogConfigurationResult> CreateAsync(Guid deviceId, HostLogSourceCreate input, Guid actor, CancellationToken token) =>
        SendAsync(Path(deviceId), new HostLogSourceCreateCommand(input, actor), token);

    public Task<HostLogConfigurationResult> UpdateAsync(Guid deviceId, Guid sourceId, HostLogSourceUpdate input, Guid actor, CancellationToken token) =>
        SendAsync($"{Path(deviceId)}/{sourceId:D}/update", new HostLogSourceUpdateCommand(input, actor), token);

    public Task<HostLogConfigurationResult> RetireAsync(Guid deviceId, Guid sourceId, string versionText, Guid actor, CancellationToken token) =>
        SendAsync($"{Path(deviceId)}/{sourceId:D}/retire", new HostLogSourceRetireCommand(versionText, actor), token);

    private async Task<HostLogConfigurationResult> SendAsync<T>(string path, T input, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, input, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(HostLogConfigurationStatus.NotFound);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid host log source.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostLogConfigurationResult>(cancellationToken: token)
            ?? throw new InvalidOperationException("Empty host log source response.");
    }

    private static string Path(Guid deviceId) => $"internal/v1/devices/{deviceId:D}/log-sources";
}
