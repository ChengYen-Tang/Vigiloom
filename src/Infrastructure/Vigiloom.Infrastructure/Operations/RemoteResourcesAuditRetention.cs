using System.Data.Common;
using System.Net.Http.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Infrastructure.Operations;

public sealed class RemoteResourcesAuditRetention(HttpClient client) : IAuditRetentionSource
{
    public Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token) =>
        SendAsync<long>("count", new(cutoff), token);

    public Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token) =>
        SendAsync<int>("delete", new(cutoff, batchSize), token);

    private async Task<T> SendAsync<T>(string action, ResourcesAuditRetentionRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/audit/retention/" + action, request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resources service returned an empty audit retention response.");
    }
}
