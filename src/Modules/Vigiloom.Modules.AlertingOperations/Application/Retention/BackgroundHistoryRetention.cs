using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

/// <summary>Removes terminal Outbox history together with its LINE delivery, if present.</summary>
internal static class BackgroundHistoryRetention
{
    internal static Task<string> DatabaseAsync(OperationsDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<string>("SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), '/', DB_NAME()) AS [Value]").SingleAsync(token);

    internal static Task<long> CountAsync(OperationsDbContext db, DateTimeOffset cutoff, CancellationToken token) =>
        db.Database.SqlQuery<long>($"""
            SELECT COUNT_BIG(*) AS [Value] FROM scheduling.Outbox
            WHERE (State = 2 AND CompletedAt < {cutoff}) OR
                  (State = 3 AND COALESCE(LastAttemptAt, CreatedAt) < {cutoff})
            """).SingleAsync(token);

    internal static async Task<int> DeleteBatchAsync(OperationsDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("A transaction is required.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            DECLARE @expired TABLE (MessageId uniqueidentifier PRIMARY KEY, MessageType nvarchar(128) NOT NULL);
            INSERT INTO @expired (MessageId, MessageType)
            SELECT TOP (@batch) MessageId, MessageType FROM scheduling.Outbox WITH (UPDLOCK)
            WHERE (State = 2 AND CompletedAt < @cutoff) OR
                  (State = 3 AND COALESCE(LastAttemptAt, CreatedAt) < @cutoff)
            ORDER BY COALESCE(CompletedAt, LastAttemptAt, CreatedAt), RowId;
            DELETE delivery FROM operations.LineDeliveries AS delivery
            INNER JOIN @expired AS expired ON expired.MessageId = delivery.Id AND expired.MessageType = N'line.push.v1';
            DELETE work FROM scheduling.Outbox AS work INNER JOIN @expired AS expired ON expired.MessageId = work.MessageId;
            SELECT COUNT(*) FROM @expired;
            """;
        Add(command, "@cutoff", DbType.DateTimeOffset, cutoff);
        Add(command, "@batch", DbType.Int32, batchSize);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static void Add(System.Data.Common.DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
