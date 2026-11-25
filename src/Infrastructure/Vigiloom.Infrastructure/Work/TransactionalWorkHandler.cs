using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Commits a service's SQL effect and durable delivery receipt together.</summary>
public sealed class TransactionalWorkHandler(
    string connectionString,
    Func<ServiceWork, SqlConnection, SqlTransaction, CancellationToken, Task<string?>> execute) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(work.DeliveryId, out var id) || id.Version != 7)
            throw new ArgumentException("A UUID v7 delivery ID is required.", nameof(work));
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { work.Kind, work.Payload }));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT PayloadHash, Result FROM scheduling.WorkReceipts WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = @id";
            lookup.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!CryptographicOperations.FixedTimeEquals((byte[])reader[0], hash))
                    throw new InvalidOperationException("The delivery ID is already associated with different work.");
                return reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        var result = await execute(work, connection, transaction, cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO scheduling.WorkReceipts (MessageId, PayloadHash, Result, CompletedAt) VALUES (@id, @hash, @result, SYSDATETIMEOFFSET())";
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
            insert.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
            insert.Parameters.Add("@result", SqlDbType.NVarChar, -1).Value = (object?)result ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
