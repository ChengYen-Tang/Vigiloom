using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Retention;

/// <summary>Applies the host log retention policy to detection evidence.</summary>
public interface ISecurityDetectionRetention
{
    Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, CancellationToken token);
    Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, int batchSize, CancellationToken token);
}
