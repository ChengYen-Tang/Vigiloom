using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal sealed class ApplicationLogDelivery(HttpClient client) : IApplicationLogDelivery
{
    public async Task<LogReceipt> SendAsync(ApplicationLogBatch batch, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/application-logs/grouped", batch, token);
        try
        {
            var receipt = await response.Content.ReadFromJsonAsync<LogReceipt>(cancellationToken: token)
                ?? throw new HttpRequestException("The Log service returned an empty response.");
            if (response.IsSuccessStatusCode != (receipt.Status == LogReceiptStatus.Accepted))
                throw new HttpRequestException("The Log service returned an inconsistent response.");
            return receipt;
        }
        catch (JsonException error) { throw new HttpRequestException("The Log service returned an invalid response.", error); }
    }
}
