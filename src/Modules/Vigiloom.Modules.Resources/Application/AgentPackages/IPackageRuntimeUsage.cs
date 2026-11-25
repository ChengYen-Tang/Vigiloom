namespace Vigiloom.Modules.Resources.Application.AgentPackages;

/// <summary>Reads installed Agent versions from the host monitoring service.</summary>
public interface IPackageRuntimeUsage
{
    Task<bool> IsInstalledAsync(string version, string runtimeIdentifier, CancellationToken token);
}
