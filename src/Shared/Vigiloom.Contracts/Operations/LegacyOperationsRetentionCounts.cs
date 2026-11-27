namespace Vigiloom.Contracts.Operations;

public sealed record LegacyOperationsRetentionCounts(long AuditRecords, long AlertEpisodes, long Notifications, long BackgroundJobs);

public interface ILegacyOperationsRetentionSource
{
    Task<LegacyOperationsRetentionCounts> CountOperationsAsync(DateTimeOffset cutoff, string excludedDatabase, CancellationToken token);
}
