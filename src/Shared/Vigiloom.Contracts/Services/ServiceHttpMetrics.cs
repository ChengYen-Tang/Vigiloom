namespace Vigiloom.Contracts.Services;

/// <summary>HTTP metrics derived from received, validated histogram observations; not a guarantee of complete traffic.</summary>
public sealed record ServiceHttpMetrics(string State, DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo,
    long? RequestCount, double? MeanDurationMilliseconds, double? ErrorRatio);
