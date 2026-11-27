using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Web.Diagnostics;

internal sealed class RemoteDeviceAuditFeed(HttpClient client)
{
    public async Task<AuditFeedPage> QueryAsync(OperationAuditQuery query, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/devices/audit", query, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditFeedPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources returned an empty device audit page.");
    }
}
