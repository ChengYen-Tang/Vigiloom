using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Agent.Baseline;

public sealed record BaselineEvidence(Guid RevisionId, DateTimeOffset ObservedAt, string OperatingSystem,
    BaselineOutcome Outcome, string? Evidence, bool Current, BaselineCheck? Definition = null);

public sealed record BaselineCheckView(string SourceKey, string CheckId, string Title,
    BaselineSourceAvailability SourceAvailability, string? UpstreamVersion, BaselineEvidence? Latest, BaselineCheck? Definition = null,
    Guid? SourceId = null, Guid? ActiveRevisionId = null, IReadOnlyList<BaselineExceptionNote>? Notes = null);

public sealed record BaselineExceptionNote(Guid SourceId, Guid RevisionId, string CheckId, string Reason, Guid ActorId, DateTimeOffset RecordedAt);
/// <summary>Administrator explanation for a specific active baseline revision.</summary>
public sealed record BaselineExceptionInput(string SourceKey, Guid RevisionId, string Reason);

public interface IBaselineExceptionReader
{
    Task<IReadOnlyList<BaselineExceptionNote>> GetRecentAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>Private baseline state for one device and its currently enrolled Agent.</summary>
public sealed record DeviceBaselineView(Guid DeviceId, Guid? AgentId, int ScanIntervalSeconds,
    IReadOnlyList<BaselineCheckView> Checks, BaselineSyncView? Sync = null);

public enum BaselineSyncState { Waiting, Pending, Applied, Failed, Offline }

public sealed record BaselineSyncView(BaselineSyncState State, string DesiredRevision, string? AppliedRevision,
    DateTimeOffset? AppliedAt, DateTimeOffset? LastSyncedAt, DateTimeOffset? ReportedAt, AgentConfigurationFailure? Failure);

public sealed record AgentBaselineSyncObservation(Guid AgentId, DateTimeOffset? LastHeartbeatAt,
    int OfflineAfterSeconds, DateTimeOffset? ReportedAt, BaselineSettingsReport? Report);

public interface IAgentBaselineSyncReader
{
    Task<AgentBaselineSyncObservation?> GetAsync(Guid deviceId, CancellationToken cancellationToken);
}
