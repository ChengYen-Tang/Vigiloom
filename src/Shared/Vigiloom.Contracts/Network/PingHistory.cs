namespace Vigiloom.Contracts.Network;

/// <summary>One historical target and measurement location; null target denotes legacy samples.</summary>
public sealed record PingHistoryOption(string? Target, string Location);
public sealed record PingHistoryQuery(Guid DeviceId, DateTimeOffset From, DateTimeOffset To, string? Target, string Location, int Page = 1, bool Descending = true);
public sealed record PingHistorySample(DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, PingOutcome? Outcome, bool Succeeded, double? LatencyMilliseconds, string? ResolvedAddress);
public sealed record PingHistoryBucket(DateTimeOffset Start, long Attempts, long Successes, long Timeouts, long Unreachable,
    long DnsErrors, long PermissionDenied, long MonitorErrors, long UnknownFailures, double? AverageMilliseconds, double? MinimumMilliseconds, double? MaximumMilliseconds, bool LatencyOverflow, DateTimeOffset LastOccurredAt);
public sealed record PingHistoryPage(IReadOnlyList<PingHistoryOption> Options, bool OptionsTruncated, IReadOnlyList<PingHistorySample> Items,
    int Page, int PageSize, long Total, IReadOnlyList<PingHistoryBucket> Buckets, bool RollupsPending, DateTimeOffset? LastObservedAt);

public interface IPingHistoryClient
{
    Task<PingHistoryPage?> QueryAsync(PingHistoryQuery query, CancellationToken token);
}
