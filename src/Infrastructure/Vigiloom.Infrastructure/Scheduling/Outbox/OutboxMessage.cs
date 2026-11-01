using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Scheduling.Outbox;

/// <summary>A durable message with a stable application-defined type, never a serialized CLR type.</summary>
public sealed record OutboxMessage(Guid MessageId, string MessageType, string Payload);

/// <summary>Handles local SQL changes in the supplied transaction; external effects need their own idempotency.</summary>
public interface IOutboxHandler
{
    string MessageType { get; }
    Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken);
}
