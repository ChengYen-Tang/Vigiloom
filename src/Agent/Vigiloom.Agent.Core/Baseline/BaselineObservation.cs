using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Core.Baseline;

/// <summary>Result of a local read-only check; evidence excludes file contents and secrets.</summary>
public sealed record BaselineObservation(BaselineOutcome Outcome, string? Evidence = null);
