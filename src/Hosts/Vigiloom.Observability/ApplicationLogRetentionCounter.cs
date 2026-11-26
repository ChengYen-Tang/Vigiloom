using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

namespace Vigiloom.Observability;

internal sealed class ApplicationLogRetentionCounter(HttpClient client) : IApplicationLogRetentionCounter
{
    private sealed record Result(long Count);
    public async Task<long> CountBeforeAsync(DateTimeOffset before, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString("internal/v1/application-logs/count", "before", before.ToString("O", CultureInfo.InvariantCulture));
        return (await client.GetFromJsonAsync<Result>(path, token)
            ?? throw new InvalidOperationException("Operations returned an empty Log count.")).Count;
    }
}
