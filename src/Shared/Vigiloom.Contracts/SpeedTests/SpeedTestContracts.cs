namespace Vigiloom.Contracts.SpeedTests;

public enum SpeedTestState { Queued, Running, Success, Partial, BudgetExceeded, TimedOut, Failed, Cancelled, Interrupted, NoServer, Unavailable }
public static class SpeedSettingsLimits
{
    public const int MinTimeoutSeconds = 40;
    public const int MaxTimeoutSeconds = 60;
    public static bool IsValid(int intervalHours, int maxMiB, int timeoutSeconds) =>
        intervalHours is >= 1 and <= 168 && maxMiB is >= 2 and <= 2048 && timeoutSeconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds;
}
public sealed record SpeedSettingsInput(bool Automatic, int IntervalHours, int MaxMiB, int TimeoutSeconds, string? Version);
public sealed record SpeedSettingsSnapshot(bool Automatic, int IntervalHours, int MaxMiB, int TimeoutSeconds,
    string Version, DateTimeOffset? NextAutomaticAt, int ReservedToday);
public sealed record SpeedRunSummary(Guid Id, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    bool Automatic, SpeedTestState State, string Location, string? Server, double? DownloadMbps, double? UploadMbps, double? LatencyMilliseconds, long? Bytes, bool CancellationRequested);
public sealed record SpeedRunDetails(SpeedRunSummary Summary, int MaxMiB, int TimeoutSeconds, string Provider,
    string? City, string? Country, string? Isp, double? Latitude, double? Longitude, string? DiagnosticCode);
public sealed record SpeedHistoryQuery(DateTimeOffset From, DateTimeOffset To, string? Search = null, string Sort = "time", bool Descending = true, int Page = 1);
public sealed record SpeedHistoryPage(IReadOnlyList<SpeedRunSummary> Items, int Total, int Page, int PageSize,
    IReadOnlyList<SpeedRunSummary> Trend, bool TrendTruncated, DateTimeOffset? EarliestAvailableAt);
public interface ISpeedHistoryClient
{
    Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token);
    Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token);
}
