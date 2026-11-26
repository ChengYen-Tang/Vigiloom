using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Web.Diagnostics;

internal sealed class RemoteHostAuditFeed(HttpClient client)
{
    public async Task<AuditFeedPage> QueryAsync(AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/audit/query", new AuditFeedRequest(source, query), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditFeedPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty audit page.");
    }
}
