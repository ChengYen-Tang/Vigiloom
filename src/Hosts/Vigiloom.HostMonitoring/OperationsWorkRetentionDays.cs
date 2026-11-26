using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal sealed class OperationsWorkRetentionDays(HttpClient client) : IHostWorkRetentionDays
{
    public async Task<int> GetAsync(CancellationToken token) =>
        (await client.GetFromJsonAsync<AuditRetentionSnapshot>("internal/v1/history/retention", token))?.Days
            ?? throw new InvalidOperationException("Operations returned an empty retention setting.");
}
