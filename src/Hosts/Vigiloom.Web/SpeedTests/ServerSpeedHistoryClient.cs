using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.SpeedTests;

internal sealed class ServerSpeedHistoryClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities, SpeedHistory history) : ISpeedHistoryClient
{
    public async Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token)
    {
        await AuthorizeAsync(token); return await history.QueryAsync(query, token);
    }
    public async Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token)
    {
        await AuthorizeAsync(token); return await history.GetAsync(id, token);
    }
    private async Task AuthorizeAsync(CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
    }
}
