namespace Vigiloom.Agent.Core.Processes;

/// <summary>A TCP listener or UDP bound endpoint observed locally. The PID is not a verified process-instance link.</summary>
public sealed record ListenerObservation(string Protocol, string Address, int Port, int? OwningProcessId);
