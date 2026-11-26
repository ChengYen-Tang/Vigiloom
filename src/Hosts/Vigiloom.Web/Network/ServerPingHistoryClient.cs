using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Network;

internal sealed class ServerPingHistoryClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    IDeviceManagement devices, PingHistoryReader history, IConfiguration configuration, IServiceProvider services) : IPingHistoryClient
{
    public async Task<PingHistoryPage?> QueryAsync(PingHistoryQuery query, CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
        if (!PingHistoryReader.Valid(query)) throw new ArgumentException("Invalid Ping history query.");
        if (await devices.GetAsync(query.DeviceId, token) is null) return null;
        if (string.IsNullOrWhiteSpace(configuration["Services:Network:Url"])) return await history.QueryAsync(query, token);
        var client = services.GetRequiredService<RemotePingHistory>();
        return await client.QueryAsync(query, token);
    }
}

internal sealed class RemotePingHistory(HttpClient client)
{
    public async Task<PingHistoryPage> QueryAsync(PingHistoryQuery query, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/ping/history", query, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PingHistoryPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned empty Ping history.");
    }
}
