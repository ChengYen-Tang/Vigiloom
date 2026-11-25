using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentPackageQuery
{
    Task<AgentPackagePage> GetAsync(int page, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentPackageView>> GetVersionsAsync(string[] versions, CancellationToken cancellationToken);
}
