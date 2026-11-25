namespace Vigiloom.Modules.AgentManagement.Application;

public interface IAgentUpdateAccess
{
    Task<byte[]?> ReadManifestAsync(string? credential, string name, CancellationToken token);
    Task<Stream?> OpenArchiveAsync(string? credential, string name, CancellationToken token);
}
