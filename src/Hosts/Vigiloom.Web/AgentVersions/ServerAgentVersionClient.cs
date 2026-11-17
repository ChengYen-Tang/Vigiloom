using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class ServerAgentVersionClient(AgentManagementSession session,
    AgentVersionAssignments assignments, AgentPackageQuery packages) : IAgentVersionClient
{
    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    { await session.ActorAsync(false, token); return await assignments.GetAsync(agentId, token); }

    public async Task<AgentPackagePage> PackagesAsync(int page, CancellationToken token)
    { await session.ActorAsync(false, token); return await packages.GetAsync(page, token); }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, AgentVersionAssignmentUpdate update, CancellationToken token)
    { return await assignments.SetAsync(agentId, update.PackageId, update.Revision, await session.ActorAsync(true, token), token); }
}
