using Vigiloom.Contracts.Operations;
using System.Data.Common;

namespace Vigiloom.Infrastructure.Operations;

public sealed class RemotePlatformAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/v1/legacy-platform-audit/retention/"), ILegacyOperationsRetentionSource
{
    public Task<LegacyOperationsRetentionCounts> CountOperationsAsync(DateTimeOffset cutoff, string excludedDatabase, CancellationToken token) =>
        SendAsync<LegacyOperationsRetentionCounts>("operations-count", new(cutoff, ExcludedOperationsDatabase: excludedDatabase), token);

    public override async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), '/', DB_NAME())";
        var database = (string)(await command.ExecuteScalarAsync(token))!;
        return await SendAsync<int>("delete", new(cutoff, batchSize, database), token);
    }
}
