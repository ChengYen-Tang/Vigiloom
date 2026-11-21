using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Operations;

/// <summary>Counts and removes a module's expired audit records in the caller's retention-policy transaction.</summary>
public interface IAuditRetentionSource
{
    Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token);
    Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token);
}
