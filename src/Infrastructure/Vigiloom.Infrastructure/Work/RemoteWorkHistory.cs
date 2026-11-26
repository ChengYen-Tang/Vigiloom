using System.Net.Http.Json;
using Vigiloom.BuildingBlocks.Operations;

namespace Vigiloom.Infrastructure.Work;

public sealed record WorkHistoryRequest(DateTimeOffset Cutoff, IReadOnlyCollection<string> ExcludedDatabases, int BatchSize = 0);

/// <summary>Calls the owner of a service database while Operations holds the retention policy lock.</summary>
public sealed class RemoteWorkHistory(HttpClient client) : IBackgroundHistorySource
{
    public Task<BackgroundHistoryResult> CountAsync(DateTimeOffset cutoff, IReadOnlyCollection<string> excludedDatabases, CancellationToken token) =>
        SendAsync<BackgroundHistoryResult>("count", new(cutoff, excludedDatabases), token);

    public Task<BackgroundHistoryResult> DeleteBatchAsync(DateTimeOffset cutoff, int batchSize, IReadOnlyCollection<string> excludedDatabases, CancellationToken token) =>
        SendAsync<BackgroundHistoryResult>("delete", new(cutoff, excludedDatabases, batchSize), token);

    private async Task<T> SendAsync<T>(string operation, WorkHistoryRequest input, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/work-history/" + operation, input, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("The service returned an empty work history response.");
    }
}
