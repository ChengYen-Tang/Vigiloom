using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Workspace;

namespace Vigiloom.Web.Authentication;

internal sealed class ServerWorkspaceIdentityClient(AuthenticationStateProvider authentication, WorkspaceIdentityService service)
    : IWorkspaceIdentityClient
{
    public async Task<WorkspaceIdentity?> GetAsync(CancellationToken cancellationToken = default)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return await service.GetAsync(state.User, cancellationToken);
    }
}
