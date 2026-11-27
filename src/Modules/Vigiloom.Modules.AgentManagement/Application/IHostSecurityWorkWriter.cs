using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Persists the security delivery request in the Host receipt transaction.</summary>
public interface IHostSecurityWorkWriter
{
    Task WriteAsync(HostMonitoringDbContext db, HostSecurityBatchReady batch, CancellationToken token);
    Task WriteStartAsync(HostMonitoringDbContext db, YaraScanStarted started, CancellationToken token);
}
