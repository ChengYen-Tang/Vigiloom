using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal sealed class ReceptionLogTimes(HttpClient client) : IReceptionLogTimes
{
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetAsync(IReadOnlyList<Guid> serviceIds, CancellationToken token)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (var batch in serviceIds.Chunk(500))
        {
            using var response = await client.PostAsJsonAsync("internal/v1/application-logs/latest-by-service", batch, token);
            response.EnsureSuccessStatusCode();
            var items = await response.Content.ReadFromJsonAsync<ApplicationLogLastReceived[]>(cancellationToken: token)
                ?? throw new InvalidOperationException("Operations returned an empty Log overview.");
            foreach (var item in items) result[item.ServiceId] = item.ReceivedAt;
        }
        return result;
    }
}
