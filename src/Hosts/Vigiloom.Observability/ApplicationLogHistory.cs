using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal sealed class ApplicationLogHistory(HttpClient client) : IApplicationLogHistory
{
    public async Task<TelemetryPage<TelemetryLog, TelemetryContext>> LogsAsync(Guid serviceId, DateTimeOffset from,
        DateTimeOffset to, int page, byte[]? traceId, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString($"internal/v1/application-logs/{serviceId:D}", new Dictionary<string, string?>
        {
            ["from"] = from.ToString("O", CultureInfo.InvariantCulture), ["to"] = to.ToString("O", CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture), ["traceId"] = traceId is null ? null : Convert.ToHexStringLower(traceId)
        });
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new ArgumentException("Invalid application Log query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TelemetryPage<TelemetryLog, TelemetryContext>>(cancellationToken: token)
            ?? throw new InvalidOperationException("The Log service returned an empty response.");
    }
}
