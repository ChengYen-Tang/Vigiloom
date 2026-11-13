namespace Vigiloom.Contracts.Agent;

/// <summary>A threshold applies to one exact registered Agent metric series.</summary>
public enum HostMetricComparison { AtLeast = 1, AtMost = 2 }
public enum HostMetricThresholdState { NotConfigured, Disabled, Unknown, Normal, Exceeded }
public enum HostMetricUnknownReason { NoSample, Stale, FutureTimestamp }

public sealed record HostMetricThresholdInput(bool Enabled, HostMetricComparison Comparison, double Value, int FreshnessSeconds, string Version);
public sealed record HostMetricThresholdView(Guid Id, Guid DeviceId, Guid AgentId, AgentMetricKind Kind, string Source,
    string? Dimension, bool Enabled, HostMetricComparison Comparison, double Value, int FreshnessSeconds, string Version);
public sealed record HostMetricThresholdStatus(HostMetricThresholdState State, HostMetricUnknownReason? UnknownReason,
    DateTimeOffset EvaluatedAt, HostMetricThresholdView? Threshold, HostMetricValue? Sample);
