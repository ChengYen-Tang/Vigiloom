using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Vigiloom.Infrastructure.Scheduling.Outbox;

/// <summary>Appends a message to the same SQL transaction as its owning module's business change.</summary>
public sealed class OutboxWriter(IEnumerable<IOutboxHandler> handlers, int? maxPayloadLength = 16384)
{
    private readonly HashSet<string> messageTypes = handlers.Select(handler => handler.MessageType).ToHashSet(StringComparer.Ordinal);

    /// <summary>Writes within the caller's active EF Core SQL Server transaction; the caller commits both changes.</summary>
    public Task WriteAsync(OutboxMessage message, DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction
            ?? throw new InvalidOperationException("An active SQL Server transaction is required.");
        if (db.Database.GetDbConnection() is not SqlConnection connection)
            throw new InvalidOperationException("A SQL Server connection is required.");
        return WriteAsync(message, connection, transaction, cancellationToken);
    }

    public async Task WriteAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (message.MessageId == Guid.Empty || message.MessageId.Version != 7)
            throw new ArgumentException("MessageId must be a UUID v7.", nameof(message));
        if (!messageTypes.Contains(message.MessageType) || message.MessageType.Length > 128)
            throw new ArgumentException("The message type must have a registered handler.", nameof(message));
        if (string.IsNullOrWhiteSpace(message.Payload) || maxPayloadLength is { } limit && message.Payload.Length > limit)
            throw new ArgumentException("The JSON payload is empty or exceeds its configured limit.", nameof(message));
        using var json = JsonDocument.Parse(message.Payload);
        if (connection.State != ConnectionState.Open || transaction.Connection != connection)
            throw new ArgumentException("An open connection and its active business transaction are required.", nameof(transaction));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scheduling.Outbox
                (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts)
            VALUES (@id, @type, @payload, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                    TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0, 0)
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = message.MessageId;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 128).Value = message.MessageType;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = message.Payload;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
