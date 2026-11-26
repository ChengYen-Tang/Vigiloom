namespace Vigiloom.BuildingBlocks.Operations;

/// <summary>Counts and removes a service's completed background dispatch history.</summary>
public interface IBackgroundHistorySource
{
    Task<BackgroundHistoryResult> CountAsync(DateTimeOffset cutoff, IReadOnlyCollection<string> excludedDatabases, CancellationToken token);
    Task<BackgroundHistoryResult> DeleteBatchAsync(DateTimeOffset cutoff, int batchSize, IReadOnlyCollection<string> excludedDatabases, CancellationToken token);
}

public sealed record BackgroundHistoryResult(string Database, long Count);
