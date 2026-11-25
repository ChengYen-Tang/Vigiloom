using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Vigiloom.Infrastructure.Scheduling.Outbox;

/// <summary>Claims bounded work with expiring SQL leases and commits each local effect with its acknowledgement.</summary>
public sealed class OutboxDispatcher(string connectionString, IEnumerable<IOutboxHandler> handlers, ILogger<OutboxDispatcher> logger, TimeSpan? handlerTimeout = null)
{
    private static readonly TimeSpan DispatchBudget = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 10;
    private const int LeaseSeconds = 120;
    private readonly Dictionary<string, IOutboxHandler> handlersByType = handlers.ToDictionary(handler => handler.MessageType, StringComparer.Ordinal);

    public async Task DispatchAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < DispatchBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaseId = Guid.CreateVersion7();
            var claim = await ClaimAsync(leaseId, cancellationToken);
            if (claim is null) return;
            var message = claim.Value.Message;
            if (!handlersByType.TryGetValue(message.MessageType, out var handler))
            {
                await FailAsync(message.MessageId, leaseId, "unknown_type", terminal: true, keepRetrying: false, cancellationToken);
                continue;
            }
            if (claim.Value.PreviousAttempts >= MaxAttempts && !handler.KeepRetrying)
            {
                await FailAsync(message.MessageId, leaseId, "attempts_exhausted", terminal: true, keepRetrying: false, cancellationToken);
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(handlerTimeout ?? TimeSpan.FromSeconds(90));
            try
            {
                await ProcessAsync(message, leaseId, handler, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await FailAsync(message.MessageId, leaseId, "handler_timeout", terminal: false, handler.KeepRetrying, cancellationToken);
            }
            catch (Exception exception)
            {
                // Exception messages and payloads can contain sensitive values. Retain only a safe code.
                logger.LogWarning("Outbox message {MessageId} failed with {ExceptionType}.", message.MessageId, exception.GetType().Name);
                await FailAsync(message.MessageId, leaseId, "handler_failed", terminal: false, handler.KeepRetrying, cancellationToken);
            }
        }
    }

    private async Task<(OutboxMessage Message, int PreviousAttempts)?> ClaimAsync(Guid leaseId, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ;WITH candidate AS
            (
                SELECT TOP (1) * FROM scheduling.Outbox WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE (State = 0 AND AvailableAt <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                   OR (State = 1 AND LeaseUntil <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                ORDER BY AvailableAt, RowId
            )
            UPDATE candidate SET State = 1, LeaseId = @lease,
                LeaseUntil = DATEADD(second, @seconds, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                LastAttemptAt = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                Attempts = CASE WHEN Attempts = 2147483647 THEN Attempts
                    WHEN Attempts >= @max AND MessageType COLLATE Latin1_General_100_BIN2 NOT IN
                        (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@keepRetryingTypes)) THEN Attempts
                    ELSE Attempts + 1 END
            OUTPUT inserted.MessageId, inserted.MessageType, inserted.Payload, deleted.Attempts;
            """;
        command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = leaseId;
        command.Parameters.Add("@seconds", SqlDbType.Int).Value = LeaseSeconds;
        command.Parameters.Add("@max", SqlDbType.Int).Value = MaxAttempts;
        command.Parameters.Add("@keepRetryingTypes", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(handlersByType.Values.Where(handler => handler.KeepRetrying).Select(handler => handler.MessageType));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? (new OutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)), reader.GetInt32(3))
            : null;
    }

    private async Task ProcessAsync(OutboxMessage message, Guid leaseId, IOutboxHandler handler, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                SELECT 1 FROM scheduling.Outbox WITH (UPDLOCK, ROWLOCK)
                WHERE MessageId = @id AND State = 1 AND LeaseId = @lease
                  AND LeaseUntil > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                """;
            claim.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = message.MessageId;
            claim.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = leaseId;
            if (await claim.ExecuteScalarAsync(token) is null) return;
        }
        await handler.HandleAsync(message, connection, transaction, token);
        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE scheduling.Outbox SET State = 2, CompletedAt = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                    LeaseId = NULL, LeaseUntil = NULL, LastFailureCode = NULL
                WHERE MessageId = @id AND State = 1 AND LeaseId = @lease
                  AND LeaseUntil > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                """;
            complete.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = message.MessageId;
            complete.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = leaseId;
            if (await complete.ExecuteNonQueryAsync(token) != 1) return;
        }
        await transaction.CommitAsync(token);
    }

    private async Task FailAsync(Guid messageId, Guid leaseId, string code, bool terminal, bool keepRetrying, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scheduling.Outbox
            SET State = CASE WHEN @terminal = 1 OR (Attempts >= @max AND @keepRetrying = 0) THEN 3 ELSE 0 END,
                AvailableAt = DATEADD(second, CONVERT(int, CASE WHEN Attempts >= 10 THEN 3600
                    ELSE IIF(10 * POWER(CAST(2 AS float), Attempts - 1) > 3600, 3600,
                             10 * POWER(CAST(2 AS float), Attempts - 1)) END), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                LeaseId = NULL, LeaseUntil = NULL, LastFailureCode = @code
            WHERE MessageId = @id AND State = 1 AND LeaseId = @lease
              AND LeaseUntil > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = messageId;
        command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = leaseId;
        command.Parameters.Add("@terminal", SqlDbType.Bit).Value = terminal;
        command.Parameters.Add("@keepRetrying", SqlDbType.Bit).Value = keepRetrying;
        command.Parameters.Add("@max", SqlDbType.Int).Value = MaxAttempts;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 128).Value = code;
        await command.ExecuteNonQueryAsync(token);
    }
}
