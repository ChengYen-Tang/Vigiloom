namespace Vigiloom.Contracts.Operations;

public sealed record LegacyOperationsRetentionCounts(long AuditRecords, long AlertEpisodes, long Notifications, long BackgroundJobs);
