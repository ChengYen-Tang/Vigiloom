using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Security;

internal sealed class OperationsHostLogs(HttpClient client) : IHostLogReader
{
    public async Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/host-logs/read", new HostLogReadRequest(agentId, messageIds), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostLogEntry[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations returned empty host log data.");
    }
}
