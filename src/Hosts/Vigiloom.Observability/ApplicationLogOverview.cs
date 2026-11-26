using System.Net.Http.Json;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

internal sealed class ApplicationLogOverview(HttpClient client) : IApplicationLogOverview
{
    private sealed record Response(DateTimeOffset? LastReceivedAt);
    public async Task<DateTimeOffset?> LastReceivedAsync(Guid serviceId, CancellationToken token) =>
        (await client.GetFromJsonAsync<Response>($"internal/v1/application-logs/{serviceId:D}/latest", token)
            ?? throw new InvalidOperationException("The Log service returned an empty response.")).LastReceivedAt;
}
