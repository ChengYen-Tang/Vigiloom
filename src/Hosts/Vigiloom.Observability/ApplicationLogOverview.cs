using System.Net.Http.Json;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

internal sealed class ApplicationLogOverview(HttpClient client, ILogger<ApplicationLogOverview> logger) : IApplicationLogOverview
{
    private sealed record Response(DateTimeOffset? LastReceivedAt);
    public async Task<DateTimeOffset?> LastReceivedAsync(Guid serviceId, CancellationToken token)
    {
        try
        {
            return (await client.GetFromJsonAsync<Response>($"internal/v1/application-logs/{serviceId:D}/latest", token)
                ?? throw new InvalidOperationException("The Log service returned an empty response.")).LastReceivedAt;
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning(error, "Unable to retrieve the latest application Log time for {ServiceId}", serviceId);
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Timed out retrieving the latest application Log time for {ServiceId}", serviceId);
            return null;
        }
    }
}
