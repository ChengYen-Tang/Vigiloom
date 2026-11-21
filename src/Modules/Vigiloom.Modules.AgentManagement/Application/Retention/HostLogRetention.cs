using System.Data;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.AgentManagement.Application.Retention;

/// <summary>Preserves logs still needed by pending detection and the correlation window.</summary>
public sealed class HostLogRetention : IHostLogRetention
{
    private const string Eligible = """
        h.ReceivedAt < @cutoff AND NOT EXISTS
        (
            SELECT 1 FROM scheduling.Outbox AS o
            WHERE o.MessageType = @type AND o.State IN (0, 1)
              AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(CASE WHEN ISJSON(o.Payload) = 1 THEN o.Payload ELSE N'{}' END, '$.AgentId')) = h.AgentId
              AND EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(o.Payload) = 1 THEN o.Payload ELSE N'{}' END, '$.MessageIds') AS ids
                          WHERE TRY_CONVERT(uniqueidentifier, ids.value) = h.MessageId)
        )
        """;

    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, CancellationToken token)
    {
        await using var command = CreateCommand(connection, transaction, now, days);
        command.CommandText = $"SELECT COUNT_BIG(*) FROM agent.HostLogs AS h WHERE {Eligible}";
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, int batchSize, CancellationToken token)
    {
        await using var command = CreateCommand(connection, transaction, now, days);
        Add(command, "@batch", DbType.Int32, batchSize);
        command.CommandText = $"""
            ;WITH expired AS
            (
                SELECT TOP (@batch) h.RowId FROM agent.HostLogs AS h
                WHERE {Eligible}
                ORDER BY h.ReceivedAt, h.RowId
            )
            DELETE h FROM agent.HostLogs AS h INNER JOIN expired AS e ON e.RowId = h.RowId;
            """;
        return await command.ExecuteNonQueryAsync(token);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var retention = TimeSpan.FromDays(days);
        Add(command, "@cutoff", DbType.DateTimeOffset, now - (retention > SigmaCorrelationLimits.RequiredRetention ? retention : SigmaCorrelationLimits.RequiredRetention));
        Add(command, "@type", DbType.String, HostLogDispatch.MessageType);
        return command;
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
