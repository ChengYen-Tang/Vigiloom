namespace Vigiloom.Contracts.Agent;

/// <summary>Current central inbox policy and cumulative cleanup progress.</summary>
public sealed record AgentInboxSettingsSnapshot(int ReplaySeconds, int RetentionDays, bool CleanupReady,
    long DeletedReceipts, DateTimeOffset? LastCleanupAt, string Version);

/// <summary>A requested inbox policy preview.</summary>
public sealed record AgentInboxPreviewRequest(int ReplaySeconds, int RetentionDays, string Version);

/// <summary>A requested inbox policy update and optional preview confirmation.</summary>
public sealed record AgentInboxSettingsUpdate(int ReplaySeconds, int RetentionDays, string Version, string? ConfirmationToken);

/// <summary>Confirms all older receiver processes and their transactions have stopped.</summary>
public sealed record AgentInboxCutoverRequest(string Version, bool ConfirmedReceiversStoppedAndDrained);

/// <summary>A point-in-time preview; retained receipt count can change as Agents send batches.</summary>
public sealed record AgentInboxSettingsPreview(int PreviousReplaySeconds, int PreviousRetentionDays,
    int ReplaySeconds, int RetentionDays, long RetainedReceipts, long AgentsWithCleanupFence,
    DateTimeOffset ObservedAt, string Version, string ConfirmationToken);
