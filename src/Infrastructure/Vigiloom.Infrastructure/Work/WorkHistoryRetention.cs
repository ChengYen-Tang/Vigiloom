using System.Data;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Removes expired terminal Temporal dispatch records owned by one service.</summary>
public sealed class WorkHistoryRetention(string connectionString)
{
    public async Task<string> DatabaseAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), '/', DB_NAME())";
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    public async Task<long> CountAsync(DateTimeOffset cutoff, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM scheduling.Outbox
            WHERE MessageType = @type AND ((State = 2 AND CompletedAt < @cutoff) OR
                (State = 3 AND COALESCE(LastAttemptAt, CreatedAt) < @cutoff));
            """;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 128).Value = TemporalOutboxHandler.Type;
        command.Parameters.Add("@cutoff", SqlDbType.DateTimeOffset).Value = cutoff;
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    public async Task<int> DeleteBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ;WITH expired AS (
                SELECT TOP (@batch) * FROM scheduling.Outbox
                WHERE MessageType = @type AND ((State = 2 AND CompletedAt < @cutoff) OR
                    (State = 3 AND COALESCE(LastAttemptAt, CreatedAt) < @cutoff))
                ORDER BY COALESCE(CompletedAt, LastAttemptAt, CreatedAt), RowId
            )
            DELETE FROM expired;
            """;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 128).Value = TemporalOutboxHandler.Type;
        command.Parameters.Add("@cutoff", SqlDbType.DateTimeOffset).Value = cutoff;
        command.Parameters.Add("@batch", SqlDbType.Int).Value = batchSize;
        return await command.ExecuteNonQueryAsync(token);
    }
}
