using System.Text.Json;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

internal static class PendingHostLogMigration
{
    internal static async Task CopyAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, string queue, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        await using var command = new SqlCommand("""
            SELECT MessageId, MessageType, Payload FROM scheduling.Outbox
            WHERE MessageType IN (@sigma, @ioc) AND State IN (0, 1) ORDER BY RowId
            """, source);
        command.Parameters.AddWithValue("@sigma", HostLogDispatch.MessageType);
        command.Parameters.AddWithValue("@ioc", HostLogDispatch.IocMessageType);
        await using var reader = await command.ExecuteReaderAsync(token);
        var writer = new OutboxWriter([new MigrationHandler()]);
        while (await reader.ReadAsync(token))
        {
            var payload = JsonSerializer.Serialize(new TemporalWorkRequest(queue, new ServiceWork(reader.GetString(1), reader.GetString(2))));
            await writer.WriteAsync(new(reader.GetGuid(0), TemporalOutboxHandler.Type, payload), target, transaction, token);
        }
    }

    private sealed class MigrationHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
