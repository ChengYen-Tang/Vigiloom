namespace Vigiloom.Contracts.Agent;

/// <summary>A point-in-time preview; retained receipt count can change as Agents send batches.</summary>
public sealed record AgentInboxSettingsPreview(int PreviousReplaySeconds, int PreviousRetentionDays,
    int ReplaySeconds, int RetentionDays, long RetainedReceipts, long AgentsWithCleanupFence,
    DateTimeOffset ObservedAt, string Version, string ConfirmationToken);
