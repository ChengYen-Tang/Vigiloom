namespace Vigiloom.Contracts.Services;

/// <summary>Discovery metadata only; this is not an OpenTelemetry payload.</summary>
public sealed record ServiceObservation(string ServiceKey, string Environment, string DisplayName, string? ServiceNamespace = null);
