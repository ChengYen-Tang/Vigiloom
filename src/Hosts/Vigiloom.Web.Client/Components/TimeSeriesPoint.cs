namespace Vigiloom.Web.Client.Components;

/// <summary>A UTC-time observation; a null value means the sample is missing.</summary>
public sealed record TimeSeriesPoint(DateTimeOffset Time, double? Value);
