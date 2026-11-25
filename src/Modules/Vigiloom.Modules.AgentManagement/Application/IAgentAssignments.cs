using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentAssignments
{
    Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token);
    Task<VersionAssignmentResult> SetAsync(Guid agentId, Guid? packageId, string? revision, Guid actorId, CancellationToken token);
}
