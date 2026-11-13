using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Workspace;

internal sealed class ServerWorkspaceOverviewClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    WorkspaceOverviewReader overview) : IWorkspaceOverviewClient
{
    public async Task<WorkspaceOverview?> GetAsync(CancellationToken cancellationToken = default)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, cancellationToken) is null) return null;
        return await overview.ReadAsync(cancellationToken);
    }
}
