using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.AgentVersions;

internal sealed class AgentManagementSession(AuthenticationStateProvider authentication, WorkspaceIdentityService identities)
{
    public async Task<Guid> ActorAsync(bool administrator, CancellationToken token)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var identity = await identities.GetAsync(principal, token);
        if (!SessionLifetime.IsCurrent(principal) || identity is null ||
            !(identity.Roles.Contains(WorkspaceAccess.Administrator) || !administrator && identity.Roles.Contains(WorkspaceAccess.ReadOnly)) ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException();
        return actor;
    }
}
