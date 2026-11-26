namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads the identities still present in the Operations Log store.</summary>
public interface IHostLogIdentities
{
    Task<IReadOnlyCollection<Guid>> ExistingAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token);
}
