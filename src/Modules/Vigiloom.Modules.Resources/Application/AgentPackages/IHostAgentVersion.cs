using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.Resources.Application.AgentPackages;

public interface IHostAgentVersion
{
    Task<AgentVersionObservation?> GetAsync(Guid agentId, CancellationToken token);
}
