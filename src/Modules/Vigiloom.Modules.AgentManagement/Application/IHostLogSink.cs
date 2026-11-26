using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Forwards validated host Logs to their Operations-owned store.</summary>
public interface IHostLogSink
{
    Task<LogReceipt> ReceiveAsync(RegisteredAgentIdentity identity, AgentLogBatch batch, CancellationToken token);
}
