using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Web.Events;

internal sealed class RemoteTimelineReader(HttpClient client)
{
    public async Task RecordAsync(OperationAudit entry, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/audit/event-access", entry, token);
        response.EnsureSuccessStatusCode();
    }
    public async Task<EventTimelinePage> GetAsync(EventTimelineQuery query, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString("internal/v1/events", new Dictionary<string, string?>
        {
            ["from"] = query.From.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["until"] = query.Until.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["groupId"] = query.GroupId?.ToString("D"), ["source"] = query.Source,
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture)
        });
        return await client.GetFromJsonAsync<EventTimelinePage>(path, token)
            ?? throw new InvalidOperationException("Operations service returned an empty timeline.");
    }
}
