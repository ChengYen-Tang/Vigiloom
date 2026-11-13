using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Network;

internal sealed class ServerPingHistoryClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    DeviceManagement devices, PingHistoryReader history) : IPingHistoryClient
{
    public async Task<PingHistoryPage?> QueryAsync(PingHistoryQuery query, CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
        if (!PingHistoryReader.Valid(query)) throw new ArgumentException("Invalid Ping history query.");
        return await devices.GetAsync(query.DeviceId, token) is null ? null : await history.QueryAsync(query, token);
    }
}
