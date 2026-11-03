namespace Vigiloom.Contracts.Network;

public enum PingOutcome { Success, Timeout, Unreachable, DnsError, PermissionDenied, MonitorError }
public enum Reachability { Unknown, Reachable, Unreachable }
public sealed record PingSettingsInput(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int SuccessThreshold, int MaxConcurrency, string? Version);
public sealed record PingSettingsSnapshot(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int SuccessThreshold, int MaxConcurrency, string Version);

public static class PingSettingsLimits
{
    public static bool IsValid(int intervalSeconds, int timeoutSeconds, int failureThreshold, int successThreshold, int maxConcurrency) =>
        intervalSeconds is >= 10 and <= 3600 && timeoutSeconds is >= 1 and <= 10 && timeoutSeconds < intervalSeconds &&
        failureThreshold is >= 1 and <= 10 && successThreshold is >= 1 and <= 10 && maxConcurrency is >= 1 and <= 256;
}
public sealed record PingTargetStatus(string Target, string? ResolvedAddress, string Location, Reachability State,
    string DisplayState, PingOutcome? LastOutcome, double? LatencyMilliseconds, DateTimeOffset? MeasuredAt, bool Stale);
public sealed record PingPolicy(int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int SuccessThreshold);
public sealed record DevicePingStatus(Guid DeviceId, IReadOnlyList<PingTargetStatus> Targets, PingPolicy? Policy = null);
