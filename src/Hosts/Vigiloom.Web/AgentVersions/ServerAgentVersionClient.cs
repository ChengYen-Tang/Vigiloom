using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.AgentVersions;

internal sealed class ServerAgentVersionClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    AgentVersionAssignments assignments, IAgentPackageQuery packages) : IAgentVersionClient
{
    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    { await ActorAsync(false, token); return await assignments.GetAsync(agentId, token); }

    public async Task<AgentPackagePage> PackagesAsync(int page, CancellationToken token)
    { await ActorAsync(false, token); return await packages.GetAsync(page, token); }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, AgentVersionAssignmentUpdate update, CancellationToken token)
    { return await assignments.SetAsync(agentId, update.PackageId, update.Revision, await ActorAsync(true, token), token); }

    private async Task<Guid> ActorAsync(bool administrator, CancellationToken token)
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
