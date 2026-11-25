namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentPackageLibrary
{
    Task<bool> CacheAsync(string version, string runtimeIdentifier, CancellationToken token);
    Task<PackageRemovalResult> RemoveAsync(Guid packageId, CancellationToken token);
}
