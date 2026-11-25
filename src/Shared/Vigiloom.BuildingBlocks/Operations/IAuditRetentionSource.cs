using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Operations;

/// <summary>Counts and removes expired audit records while the caller holds the retention-policy lock.
/// Co-located sources use the caller's transaction; remote sources commit in their own database.</summary>
public interface IAuditRetentionSource
{
    Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token);
    Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token);
}
