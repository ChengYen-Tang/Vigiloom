using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class ServerAgentRolloutClient(AgentManagementSession session, AgentRollouts rollouts) : IAgentRolloutClient
{
    public async Task<IReadOnlyList<AgentRolloutSummary>> ListAsync(Guid groupId, CancellationToken token)
    { await session.ActorAsync(false, token); return await rollouts.ListAsync(groupId, token); }
    public async Task<AgentRolloutView?> GetAsync(Guid id, CancellationToken token)
    { await session.ActorAsync(false, token); return await rollouts.GetAsync(id, token); }
    public async Task<AgentRolloutPreview?> PreviewAsync(Guid groupId, Guid packageId, CancellationToken token)
    { await session.ActorAsync(false, token); return await rollouts.PreviewAsync(groupId, packageId, token); }
    public async Task<AgentRolloutCreation> CreateAsync(AgentRolloutInput input, CancellationToken token) =>
        await rollouts.CreateAsync(input, await session.ActorAsync(true, token), token);
    public async Task<AgentRolloutResult> ChangeAsync(Guid id, AgentRolloutChange input, CancellationToken token) =>
        await rollouts.ChangeAsync(id, input.Revision, input.Action, await session.ActorAsync(true, token), token);
}
