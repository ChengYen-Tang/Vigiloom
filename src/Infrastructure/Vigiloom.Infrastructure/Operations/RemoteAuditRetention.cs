using System.Data.Common;
using System.Net.Http.Json;
using Vigiloom.BuildingBlocks.Operations;

namespace Vigiloom.Infrastructure.Operations;

public abstract class RemoteAuditRetention(HttpClient client, string path) : IAuditRetentionSource
{
    public Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token) =>
        SendAsync<long>("count", new(cutoff), token);

    public Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token) =>
        SendAsync<int>("delete", new(cutoff, batchSize), token);

    private async Task<T> SendAsync<T>(string action, Request request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path + action, request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("The service returned an empty audit retention response.");
    }

    private sealed record Request(DateTimeOffset Cutoff, int BatchSize = 0);
}
