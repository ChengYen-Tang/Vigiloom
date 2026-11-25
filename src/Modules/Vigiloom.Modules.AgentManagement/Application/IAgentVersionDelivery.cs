namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentVersionDelivery
{
    Task<AgentVersionAssignments.AgentRead> GetForAgentAsync(string? credential, CancellationToken token);
}
