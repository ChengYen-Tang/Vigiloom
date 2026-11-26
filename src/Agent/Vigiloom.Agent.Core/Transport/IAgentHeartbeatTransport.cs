using System.Net;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Returns null when no live connection is available and HTTP should be used.</summary>
public interface IAgentHeartbeatTransport
{
    Task<HttpStatusCode?> SendAsync(Uri server, string credential, AgentHeartbeatReport? report, CancellationToken token);
}
