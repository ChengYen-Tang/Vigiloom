using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Contracts.Agent.Baseline;

public sealed record BaselineEvidence(Guid RevisionId, DateTimeOffset ObservedAt, string OperatingSystem,
    BaselineOutcome Outcome, string? Evidence, bool Current);

public sealed record BaselineCheckView(string SourceKey, string CheckId, string Title,
    BaselineSourceAvailability SourceAvailability, string? UpstreamVersion, BaselineEvidence? Latest);

/// <summary>Private baseline state for one device and its currently enrolled Agent.</summary>
public sealed record DeviceBaselineView(Guid DeviceId, Guid? AgentId, int ScanIntervalSeconds,
    IReadOnlyList<BaselineCheckView> Checks);
