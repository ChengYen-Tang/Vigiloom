namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentRegistration
{
    Task<DeviceAgents?> GetDeviceAgentsAsync(Guid deviceId, CancellationToken token);
    Task<EnrollmentIssue> IssueAsync(Guid deviceId, Guid actorId, CancellationToken token);
    Task<AgentIdentityStatus> RevokeAsync(Guid agentId, Guid actorId, CancellationToken token);
    Task<AgentIdentityStatus> RevokeEnrollmentAsync(Guid enrollmentId, Guid actorId, CancellationToken token);
}
