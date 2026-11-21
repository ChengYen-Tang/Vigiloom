using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Retention;

/// <summary>Applies the log policy to host records inside the caller's transaction.</summary>
public interface IHostLogRetention
{
    Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, CancellationToken token);
    Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, int batchSize, CancellationToken token);
}
