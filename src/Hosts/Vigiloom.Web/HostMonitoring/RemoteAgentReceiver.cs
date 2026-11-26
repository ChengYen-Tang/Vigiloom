using System.Net.Http.Json;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteAgentReceiver(HttpClient client) : IAgentReceiverMonitoring
{
    public async Task<DateTimeOffset?> LastCommittedBatchAsync(CancellationToken token) =>
        (await client.GetFromJsonAsync<AgentReceiverSnapshot>("internal/v1/agent-receiver/status", token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty receiver status.")).LastCommittedBatchAt;
}
