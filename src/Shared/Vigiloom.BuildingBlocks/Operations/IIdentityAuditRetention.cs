using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Operations;

/// <summary>Counts and removes expired identity audit records within the caller's retention-policy transaction.</summary>
public interface IIdentityAuditRetention
{
    Task<(long Accounts, long Credentials)> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token);
    Task<(int Accounts, int Credentials)> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token);
}
