namespace Vigiloom.Contracts.Agent.Integrity;

public sealed record IntegrityScopeMonitoringView(Guid ScopeId, long ConfiguredRevision, IntegrityScopeKind Kind,
    string Root, bool Enabled, DateTimeOffset? LastAttemptAt, IntegrityScanOutcome? LastOutcome,
    IntegrityCompletion? LastStatus, IntegrityPartialReason? LastPartialReason, bool? CoverageGap,
    DateTimeOffset? LastCompleteAt, Guid? SnapshotId, long? BaselineRevision, DateTimeOffset? SnapshotAt,
    int? ChunkCount, int? TotalItems);

public sealed record DeviceIntegrityView(Guid DeviceId, Guid? AgentId, IReadOnlyList<IntegrityScopeMonitoringView> Scopes);
public sealed record IntegritySnapshotChunkView(Guid SnapshotId, Guid ScopeId, long ScopeRevision,
    DateTimeOffset ObservedAt, int ChunkIndex, int ChunkCount, IReadOnlyList<IntegrityItem> Items);
public enum IntegrityChangeType { Added = 1, Modified = 2, Removed = 3 }
public sealed record IntegrityChangeView(Guid AgentId, Guid ScopeId, long ScopeRevision, DateTimeOffset ObservedAt,
    IntegrityChangeType Kind, IntegrityItem? Before, IntegrityItem? After, bool CoverageGap);
public sealed record IntegrityChangePage(IReadOnlyList<IntegrityChangeView> Items, string? NextCursor);
