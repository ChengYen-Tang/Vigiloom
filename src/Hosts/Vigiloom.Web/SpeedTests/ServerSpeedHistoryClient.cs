using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.SpeedTests;

internal sealed class ServerSpeedHistoryClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities, SpeedHistory history,
    IConfiguration configuration, IServiceProvider services) : ISpeedHistoryClient
{
    private RemoteSpeedHistory? Remote => string.IsNullOrWhiteSpace(configuration["Services:Network:Url"]) ? null : services.GetRequiredService<RemoteSpeedHistory>();
    public async Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token)
    {
        await AuthorizeAsync(token);
        if (!SpeedHistory.Valid(query)) throw new ArgumentException("Invalid speed history query.");
        return Remote is { } remote ? await remote.QueryAsync(query, token) : await history.QueryAsync(query, token);
    }
    public async Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token)
    {
        await AuthorizeAsync(token); return Remote is { } remote ? await remote.GetAsync(id, token) : await history.GetAsync(id, token);
    }
    private async Task AuthorizeAsync(CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null) throw new UnauthorizedAccessException();
    }
}
