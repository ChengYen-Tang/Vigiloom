namespace Vigiloom.Agent.Core.Processes;

/// <summary>A locally observed OS service, separate from an application service.</summary>
public sealed record ServiceObservation(string Key, string Name, string Status, string? StartupMode);
