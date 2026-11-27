using System.Data.Common;
using System.Net.Http.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Infrastructure.Work;

/// <summary>The caller holds its policy lock while Security commits each independent batch.</summary>
public sealed class RemoteSecurityRetention(HttpClient client) : IAuditRetentionSource, ISecurityDetectionRetention
{
    public Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token) =>
        PostAsync<long>("internal/v1/retention/audit/count", new AuditRetentionRequest(cutoff), token);
    public Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token) =>
        PostAsync<int>("internal/v1/retention/audit/delete", new AuditRetentionRequest(cutoff, batchSize), token);
    public Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, CancellationToken token) =>
        PostAsync<long>("internal/v1/retention/detections/count", new SecurityDetectionRetentionRequest(now, days), token);
    public Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, int batchSize, CancellationToken token) =>
        PostAsync<int>("internal/v1/retention/detections/delete", new SecurityDetectionRetentionRequest(now, days, batchSize), token);

    private async Task<T> PostAsync<T>(string path, object request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty retention result.");
    }
}
