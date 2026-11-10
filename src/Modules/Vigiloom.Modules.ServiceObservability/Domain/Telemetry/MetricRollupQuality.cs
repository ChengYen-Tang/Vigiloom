namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>Reasons a window result may be incomplete or require retaining its source data.</summary>
[Flags]
public enum MetricRollupQuality : long
{
    None = 0,
    Missing = 1L << 0,
    Invalid = 1L << 1,
    Gap = 1L << 2,
    Overlap = 1L << 3,
    CrossesBoundary = 1L << 4,
    Incompatible = 1L << 5,
    Overflow = 1L << 6,
    Capacity = 1L << 7,
    Unsupported = 1L << 8,
    ExemplarsRequireRaw = 1L << 9,
    StatisticsUnverified = 1L << 10,
    UnknownStart = 1L << 11,
    AmbiguousTimestamp = 1L << 12,
    Decreased = 1L << 13,
    MonotonicityUnknown = 1L << 14,
    NoResult = 1L << 15
}
