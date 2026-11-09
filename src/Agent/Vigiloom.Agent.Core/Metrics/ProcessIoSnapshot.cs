namespace Vigiloom.Agent.Core.Metrics;

/// <summary>Monotonic logical read and write bytes attributed to the Agent process by its OS.</summary>
public readonly record struct ProcessIoSnapshot(ulong ReadBytes, ulong WriteBytes);
