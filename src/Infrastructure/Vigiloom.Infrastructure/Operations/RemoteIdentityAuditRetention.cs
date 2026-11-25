using System.Data.Common;
using System.Net.Http.Json;
using Vigiloom.BuildingBlocks.Operations;

namespace Vigiloom.Infrastructure.Operations;

public sealed record IdentityAuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0);

/// <summary>Coordinates the Identity-owned audit data while the caller holds the retention policy lock.</summary>
public sealed class RemoteIdentityAuditRetention(HttpClient client) : IAuditRetentionSource
{
    public Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token) =>
        SendAsync<long>("count", new(cutoff), token);

    public Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token) =>
        SendAsync<int>("delete", new(cutoff, batchSize), token);

    private async Task<T> SendAsync<T>(string action, IdentityAuditRetentionRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/audit/retention/" + action, request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty audit retention response.");
    }
}
